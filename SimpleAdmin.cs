using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Commands.Targeting;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using Microsoft.Extensions.Logging;

namespace SimpleAdmin;

[MinimumApiVersion(132)]
public class SimpleAdmin : BasePlugin
{
    public override string ModuleName => "SimpleAdmin";
    public override string ModuleVersion => "0.3.0";

    private string apiBase = "http://127.0.0.1:5000/api";
    private string? apiKey;
    private int timeoutMs = 1500;
    private int maxRetries = 2;
    private int initialBackoffMs = 200;
    private static readonly HttpClient httpClient = new HttpClient();

    public static bool IsValidPlayer(CCSPlayerController? player, bool can_be_bot = false)
    {
        return player != null && player.IsValid && (!player.IsBot || can_be_bot);
    }

    [RequiresPermissions("@css/ban")]
    [CommandHelper(minArgs: 1, usage: "<target | steamID64> [minutes]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [ConsoleCommand("css_ban", "Ban a user")]
    public async Task OnCommandBan(CCSPlayerController _, CommandInfo command)
    {
        var args = command.ArgString.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (args.Count == 0)
        {
            command.ReplyToCommand($"[CSS] Expected usage: {command.GetArg(0)} <target | steamID64> [minutes]");
            return;
        }

        ulong minutes = 0;
        if (args.Count > 1 && !ulong.TryParse(args[1], out minutes))
        {
            command.ReplyToCommand("Invalid ban length");
            command.ReplyToCommand($"[CSS] Expected usage: {command.GetArg(0)} <target | steamID64> [minutes]");
            return;
        }

        var targetedUsers = new Target(args[0]).GetTarget(command.CallingPlayer).Where(p => p is { IsBot: false }).ToList();

        if (targetedUsers.Count > 1)
        {
            command.ReplyToCommand($"Identifier {args[0]} targets more than one person");
            command.ReplyToCommand($"[CSS] Expected usage: {command.GetArg(0)} <target | steamID64> [minutes]");
            return;
        }

        if (targetedUsers.Count == 1)
        {
            var userToBan = targetedUsers.First();
            var bu = new BannedUser(userToBan);
            var result = await PostBanRemoteSafeAsync(bu, minutes).ConfigureAwait(false);
            if (result.Success && result.IsBanned)
            {
                Server.ExecuteCommand($"kickid {userToBan.UserId}");
                command.ReplyToCommand($"{bu.PlayerName ?? "[unnamed]"} with Steam ID {bu.SteamID} has been banned{(minutes > 0 ? $" for {minutes} minutes" : "")}.");
                return;
            }

            if (!result.Success)
            {
                command.ReplyToCommand("Remote API unavailable; ban not applied. Check logs.");
                return;
            }

            command.ReplyToCommand("Failed to ban user via remote API.");
            return;
        }

        var targetString = args[0].TrimStart('#');
        if (targetString.Length == 17 && UInt64.TryParse(targetString, out var steamId))
        {
            var user = new BannedUser { SteamID = steamId, PlayerName = null };
            var result = await PostBanRemoteSafeAsync(user, minutes).ConfigureAwait(false);
            if (result.Success && result.IsBanned)
            {
                command.ReplyToCommand($"{user.PlayerName ?? "[unnamed]"} with Steam ID {user.SteamID} has been banned{(minutes > 0 ? $" for {minutes} minutes" : "")}.");
                return;
            }

            if (!result.Success)
            {
                command.ReplyToCommand("Remote API unavailable; ban not applied. Check logs.");
                return;
            }

            command.ReplyToCommand("Failed to ban user via remote API.");
            return;
        }

        command.ReplyToCommand($"Couldn't find user by identifier {args[0]}");
        command.ReplyToCommand($"[CSS] Expected usage: {command.GetArg(0)} <target | steamID64>");
    }

    [RequiresPermissions("@css/unban")]
    [CommandHelper(minArgs: 1, usage: "<steam id | username>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [ConsoleCommand("css_unban", "Unban a user")]
    public async Task OnCommandUnban(CCSPlayerController _, CommandInfo command)
    {
        var identifier = command.GetArg(1);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            command.ReplyToCommand($"[CSS] Expected usage: {command.GetArg(0)} <steam_id | username>");
            return;
        }

        if (UInt64.TryParse(identifier, out var steamId))
        {
            var del = await DeleteBanRemoteSafeAsync(steamId).ConfigureAwait(false);
            if (del.Success && del.Deleted)
            {
                command.ReplyToCommand($"Unbanned Steam ID {steamId} via remote API.");
                return;
            }

            if (!del.Success)
            {
                command.ReplyToCommand("Remote API unavailable; unban not applied. Check logs.");
                return;
            }

            command.ReplyToCommand($"Failed to unban Steam ID {steamId} via remote API.");
            return;
        }

        var lookup = await GetBanByUsernameRemoteSafeAsync(identifier).ConfigureAwait(false);
        if (!lookup.Success)
        {
            command.ReplyToCommand("Remote API unavailable; unban not applied. Check logs.");
            return;
        }

        if (lookup.Ban == null)
        {
            command.ReplyToCommand($"No remote ban found for username {identifier}");
            return;
        }

        var del2 = await DeleteBanRemoteSafeAsync(lookup.Ban.SteamID).ConfigureAwait(false);
        if (del2.Success && del2.Deleted)
        {
            command.ReplyToCommand($"Unbanned {lookup.Ban.PlayerName} ({lookup.Ban.SteamID}) via remote API.");
            return;
        }

        if (!del2.Success)
        {
            command.ReplyToCommand("Remote API unavailable; unban not applied. Check logs.");
            return;
        }

        command.ReplyToCommand($"Failed to unban {lookup.Ban.PlayerName} via remote API.");
    }

    [RequiresPermissions("@css/kick")]
    [CommandHelper(minArgs: 1, usage: "<target>", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    [ConsoleCommand("css_kick", "Kick a user")]
    public void OnCommandKick(CCSPlayerController _, CommandInfo command)
    {
        var target = command.GetArgTargetResult(1);
        if (!target.Players.Any())
        {
            command.ReplyToCommand($"Couldn't find user by identifier {command.GetArg(1)}");
            return;
        }

        if (target.Players.Count > 1)
        {
            command.ReplyToCommand($"Identifier {command.GetArg(1)} targets more than one person");
            return;
        }

        var userToKick = target.Players.First();
        if (userToKick != null)
        {
            Server.ExecuteCommand($"kickid {userToKick.UserId}");
            return;
        }

        command.ReplyToCommand($"[CSS] Expected usage: {command.GetArg(0)} <target>");
    }

    [ConsoleCommand("css_players", "Get a list of current players")]
    public void OnCommandPlayers(CCSPlayerController _, CommandInfo command)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (IsValidPlayer(player))
            {
                command.ReplyToCommand($"{player.UserId}: {player.PlayerName}");
            }
        }
    }

    public override void Load(bool hotReload)
    {
        var envBase = Environment.GetEnvironmentVariable("SIMPLEADMIN_API_BASE");
        var envKey  = Environment.GetEnvironmentVariable("SIMPLEADMIN_API_KEY");

        if (string.IsNullOrWhiteSpace(envBase))
        {
            throw new Exception("SIMPLEADMIN_API_BASE is required but was not provided.");
        }

        if (string.IsNullOrWhiteSpace(envKey))
        {
            throw new Exception("SIMPLEADMIN_API_KEY is required but was not provided.");
        }

        apiBase = envBase.TrimEnd('/');
        apiKey = envKey;

        var tms = Environment.GetEnvironmentVariable("SIMPLEADMIN_TIMEOUT_MS");
        if (int.TryParse(tms, out var parsedTms) && parsedTms > 0) timeoutMs = parsedTms;

        var retries = Environment.GetEnvironmentVariable("SIMPLEADMIN_RETRIES");
        if (int.TryParse(retries, out var parsedRetries) && parsedRetries >= 0) maxRetries = parsedRetries;

        var backoff = Environment.GetEnvironmentVariable("SIMPLEADMIN_BACKOFF_MS");
        if (int.TryParse(backoff, out var parsedBackoff) && parsedBackoff >= 0) initialBackoffMs = parsedBackoff;

        RegisterListener<Listeners.OnClientConnected>((slot) =>
        {
            _ = HandleClientConnectedAsync(slot);
        });

        Logger.LogInformation($"SimpleAdmin loaded. API base: {apiBase}");
    }


    private async Task HandleClientConnectedAsync(int slot)
    {
        try
        {
            var newPlayer = Utilities.GetPlayerFromSlot(slot);
            var apiCheck = await TryGetBanAsync(newPlayer.SteamID).ConfigureAwait(false);
            if (!apiCheck.Ok)
            {
                Logger.LogWarning("SimpleAdmin: remote API check failed for {steamid}; allowing join", newPlayer.SteamID);
                return;
            }

            if (apiCheck.Ban != null)
            {
                Logger.LogInformation("SimpleAdmin: remote banned user {username} tried to join", newPlayer.PlayerName ?? "[unnamed]");
                Server.ExecuteCommand($"kickid {newPlayer.UserId}");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during OnClientConnected ban check");
        }
    }

    private async Task<(bool Ok, BannedUser? Ban)> TryGetBanAsync(ulong steamId)
    {
        var attempt = 0;
        var backoff = initialBackoffMs;
        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/bans/{steamId}");
                AddApiKeyHeaderIfPresent(req);
                using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (true, null);
                if (!resp.IsSuccessStatusCode)
                {
                    if (attempt > maxRetries) return (false, null);
                    await Task.Delay(backoff).ConfigureAwait(false);
                    backoff = checked(backoff * 2);
                    continue;
                }

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) return (true, null);
                var b = ParseBannedUserFromJson(content);
                return (true, b);
            }
            catch (OperationCanceledException)
            {
                if (attempt > maxRetries) return (false, null);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SimpleAdmin: error contacting remote API (attempt {attempt})", attempt);
                if (attempt > maxRetries) return (false, null);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
        }
    }

    private async Task<(bool Success, bool IsBanned)> PostBanRemoteSafeAsync(BannedUser user, ulong minutes)
    {
        var attempt = 0;
        var backoff = initialBackoffMs;
        var payload = new { steamId = user.SteamID.ToString(), username = user.PlayerName ?? string.Empty, minutes = minutes };

        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{apiBase}/bans");
                AddApiKeyHeaderIfPresent(req);
                var json = JsonSerializer.Serialize(payload);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return (true, true);
                if (resp.StatusCode == System.Net.HttpStatusCode.Conflict) return (true, true);
                if (attempt > maxRetries) return (false, false);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
            catch (OperationCanceledException)
            {
                if (attempt > maxRetries) return (false, false);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SimpleAdmin: error posting ban to remote API (attempt {attempt})", attempt);
                if (attempt > maxRetries) return (false, false);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
        }
    }

    private async Task<(bool Success, bool Deleted)> DeleteBanRemoteSafeAsync(ulong steamId)
    {
        var attempt = 0;
        var backoff = initialBackoffMs;
        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var req = new HttpRequestMessage(HttpMethod.Delete, $"{apiBase}/bans/{steamId}");
                AddApiKeyHeaderIfPresent(req);
                using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return (true, true);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (true, false);
                if (attempt > maxRetries) return (false, false);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
            catch (OperationCanceledException)
            {
                if (attempt > maxRetries) return (false, false);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SimpleAdmin: error deleting ban via remote API (attempt {attempt})", attempt);
                if (attempt > maxRetries) return (false, false);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
        }
    }

    private async Task<(bool Success, BannedUser? Ban)> GetBanByUsernameRemoteSafeAsync(string username)
    {
        var attempt = 0;
        var backoff = initialBackoffMs;
        while (true)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                var url = $"{apiBase}/bans?username={Uri.EscapeDataString(username)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddApiKeyHeaderIfPresent(req);
                using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if (attempt > maxRetries) return (false, null);
                    await Task.Delay(backoff).ConfigureAwait(false);
                    backoff = checked(backoff * 2);
                    continue;
                }

                var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content)) return (true, null);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var firstJson = root[0].GetRawText();
                    var b = ParseBannedUserFromJson(firstJson);
                    return (true, b);
                }

                if (root.ValueKind == JsonValueKind.Object)
                {
                    var b = ParseBannedUserFromJson(root.GetRawText());
                    return (true, b);
                }

                return (true, null);
            }
            catch (OperationCanceledException)
            {
                if (attempt > maxRetries) return (false, null);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "SimpleAdmin: error fetching ban by username (attempt {attempt})", attempt);
                if (attempt > maxRetries) return (false, null);
                await Task.Delay(backoff).ConfigureAwait(false);
                backoff = checked(backoff * 2);
            }
        }
    }

    private void AddApiKeyHeaderIfPresent(HttpRequestMessage req)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            if (req.Headers.Contains("x-api-key"))
            {
                req.Headers.Remove("x-api-key");
            }
            req.Headers.Add("x-api-key", apiKey);
        }
    }

    private BannedUser? ParseBannedUserFromJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var b = new BannedUser();
            if (root.TryGetProperty("steamId", out var sidProp))
            {
                var s = sidProp.GetString();
                if (!string.IsNullOrWhiteSpace(s) && UInt64.TryParse(s, out var sid)) b.SteamID = sid;
            }
            else if (root.TryGetProperty("steam_id", out var sidProp2))
            {
                var s = sidProp2.GetString();
                if (!string.IsNullOrWhiteSpace(s) && UInt64.TryParse(s, out var sid)) b.SteamID = sid;
            }

            if (root.TryGetProperty("username", out var uname)) b.PlayerName = uname.GetString();
            return b.SteamID != 0 ? b : null;
        }
        catch
        {
            return null;
        }
    }

    private class ApiBanResult
    {
        public bool Success { get; set; }
        public bool IsBanned { get; set; }
    }

    private class ApiDeleteResult
    {
        public bool Success { get; set; }
        public bool Deleted { get; set; }
    }

    private class ApiLookupResult
    {
        public bool Success { get; set; }
        public BannedUser? Ban { get; set; }
    }

    private class BannedUser
    {
        public UInt64 SteamID { get; set; }
        public string? PlayerName { get; set; }
        public bool ServedTheirTime { get; set; }

        public BannedUser() { }

        public BannedUser(CCSPlayerController player)
        {
            SteamID = player.SteamID;
            PlayerName = player.PlayerName;
            ServedTheirTime = false;
        }
    }
}
