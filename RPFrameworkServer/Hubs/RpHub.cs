using Microsoft.AspNetCore.SignalR;
using RPFramework.Contracts;
using RPFrameworkServer.Services;
using Ev = RPFramework.Contracts.HubRoutes.Events;

namespace RPFrameworkServer.Hubs;

/// <summary>
/// Server-first SignalR hub. Clients send INTENTS (these methods); the server validates
/// identity + role, mutates the authoritative state via <see cref="SessionManager"/>, and
/// broadcasts authoritative aggregate snapshots. Method names match
/// <see cref="HubRoutes.Intents"/>; event names match <see cref="HubRoutes.Events"/>.
/// </summary>
public class RpHub : Hub
{
    private readonly SessionManager  _sessions;
    private readonly BgmAudioService _audio;
    private readonly ILogger<RpHub>  _log;

    public RpHub(SessionManager sessions, BgmAudioService audio, ILogger<RpHub> log)
    {
        _sessions = sessions;
        _audio    = audio;
        _log      = log;
    }

    // ── Per-connection identity ───────────────────────────────────────────────
    private string? Pid => Context.Items.TryGetValue("pid", out var v) ? v as string : null;
    private string  Dn  => Context.Items.TryGetValue("dn",  out var v) ? v as string ?? "" : "";
    private string  RequirePid() => Pid ?? throw new HubException("Call Identify first.");
    private static string PartyGroup(string code) => "party:" + code;

    private Task Fail(string ctx, string msg) => Clients.Caller.SendAsync(Ev.Error, ctx, msg);

    /// <summary>Sends to every live connection of a player (identity is via Identify, not auth, so we can't use Clients.User).</summary>
    private Task SendToPlayer(string playerId, string method, object arg)
    {
        var conns = _sessions.ConnectionsOf(playerId);
        return conns.Count == 0 ? Task.CompletedTask : Clients.Clients(conns.ToArray()).SendAsync(method, arg);
    }

    /// <summary>
    /// Broadcasts a character update to a campaign, per-recipient: the owner and DMs (Owner/CoDm) get the
    /// full state; everyone else gets it with DM-only skills stripped server-side (real privacy, not just
    /// a UI hide). Replaces the old single group send. <paramref name="except"/> skips one player (e.g. the
    /// caller, who already got their own snapshot).
    /// </summary>
    private async Task BroadcastCharacterAsync(string code, CharacterDto ch, string? except = null)
    {
        var viewers = await _sessions.CampaignViewersAsync(code);
        CharacterDto? stripped = null;
        foreach (var (pid, isDm) in viewers)
        {
            if (pid == except) continue;
            bool full = isDm || pid == ch.OwnerPlayerId;
            var dto = full ? ch : (stripped ??= SessionManager.StripDmSkills(ch));
            await SendToPlayer(pid, Ev.CharacterUpdated, dto);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Identify + connection lifecycle
    // ═════════════════════════════════════════════════════════════════════════

    public async Task Identify(string playerId, string secret, string displayName)
    {
        if (!InputSanitizer.IsValidPlayerId(playerId)) { await Fail("Identify", "Invalid player id."); return; }
        displayName = InputSanitizer.SanitizeName(displayName);
        secret      = InputSanitizer.SanitizeName(secret, 128);

        if (!await _sessions.AuthenticateAsync(playerId, secret, displayName))
        {
            await Fail("Identify", "Identity is already claimed by another client.");
            return;
        }

        Context.Items["pid"] = playerId;
        Context.Items["dn"]  = displayName;
        bool cameOnline = _sessions.AddConnection(playerId, displayName, Context.ConnectionId);
        await _sessions.EnsurePersonalCampaignAsync(playerId, displayName);

        var snapshot = await _sessions.BuildSnapshotAsync(playerId);
        foreach (var party in snapshot.Parties)
            await Groups.AddToGroupAsync(Context.ConnectionId, PartyGroup(party.Code));
        // Re-add to the groups of every room the player is a persistent member of, so live
        // playback reaches them with no client-side rejoin (membership survives reconnects).
        foreach (var roomCode in await _sessions.PlayerRoomCodesAsync(playerId))
            await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(roomCode));
        await Clients.Caller.SendAsync(Ev.Snapshot, snapshot);

        if (cameOnline)
            foreach (var party in snapshot.Parties)
                await Clients.OthersInGroup(PartyGroup(party.Code)).SendAsync(Ev.PartyUpdated, party);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var (playerId, wentOffline) = _sessions.RemoveConnection(Context.ConnectionId);
        if (playerId != null && wentOffline)
        {
            var snapshot = await _sessions.BuildSnapshotAsync(playerId);
            foreach (var party in snapshot.Parties)
                await Clients.Group(PartyGroup(party.Code)).SendAsync(Ev.PartyUpdated, party);
            // BGM room membership is persistent — the player is NOT removed from rooms on disconnect.
        }
        await base.OnDisconnectedAsync(exception);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Parties / campaigns
    // ═════════════════════════════════════════════════════════════════════════

    public async Task PartyCreate(string name, string? password)
    {
        var pid = RequirePid();
        var res = await _sessions.CreateCampaignAsync(pid, Dn, name, password);
        if (res == null) { await Fail("PartyCreate", "Could not create campaign."); return; }
        var (party, ch, template) = res.Value;
        await Groups.AddToGroupAsync(Context.ConnectionId, PartyGroup(party.Code));
        await Clients.Caller.SendAsync(Ev.PartyUpdated, party);
        await Clients.Caller.SendAsync(Ev.TemplateUpdated, template);
        await Clients.Caller.SendAsync(Ev.CharacterUpdated, ch);
    }

    public async Task PartyJoin(string code, string? password)
    {
        var pid = RequirePid();
        if (!InputSanitizer.IsValidCode(code)) { await Fail("PartyJoin", "Invalid code."); return; }
        var (result, party, ch, template) = await _sessions.JoinCampaignAsync(pid, Dn, code, password);
        switch (result)
        {
            case SessionManager.JoinResult.NotFound:    await Fail("PartyJoin", "No such campaign."); return;
            case SessionManager.JoinResult.BadPassword: await Fail("PartyJoin", "Wrong password.");   return;
            case SessionManager.JoinResult.Full:        await Fail("PartyJoin", "Campaign is full."); return;
            case SessionManager.JoinResult.AlreadyMember:
                await Groups.AddToGroupAsync(Context.ConnectionId, PartyGroup(code)); return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, PartyGroup(code));
        // Rehydrate the joiner fully so they get the template AND every existing member's character
        // — not just their own. Otherwise the joiner can't see other members' sheets until those
        // members happen to edit (which would broadcast to the group).
        await Clients.Caller.SendAsync(Ev.Snapshot, await _sessions.BuildSnapshotAsync(pid));
        // Tell the rest of the party about the new member + their character.
        await Clients.Group(PartyGroup(code)).SendAsync(Ev.PartyUpdated, party!);
        await BroadcastCharacterAsync(code, ch!, except: pid);
    }

    public async Task PartyLeave(string code)
    {
        var pid = RequirePid();
        var party = await _sessions.LeaveCampaignAsync(code, pid);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, PartyGroup(code));
        if (party != null)
            await Clients.Group(PartyGroup(code)).SendAsync(Ev.PartyUpdated, party);
    }

    public async Task PartyDisband(string code)
    {
        var pid = RequirePid();
        var members = await _sessions.DisbandCampaignAsync(code, pid);
        if (members == null) { await Fail("PartyDisband", "Not allowed."); return; }
        await Clients.Group(PartyGroup(code)).SendAsync(Ev.PartyDisbanded, code);
    }

    public async Task PartyKick(string code, string targetPlayerId)
    {
        var pid = RequirePid();
        if (!await _sessions.IsDmAsync(code, pid)) { await Fail("PartyKick", "Not allowed."); return; }
        var party = await _sessions.LeaveCampaignAsync(code, targetPlayerId);
        foreach (var conn in _sessions.ConnectionsOf(targetPlayerId))
            await Groups.RemoveFromGroupAsync(conn, PartyGroup(code));
        if (party != null)
        {
            await Clients.Group(PartyGroup(code)).SendAsync(Ev.PartyUpdated, party);
            await SendToPlayer(targetPlayerId, Ev.PartyDisbanded, code); // best-effort; client also drops it
        }
    }

    public async Task PartySetRole(string code, string targetPlayerId, PartyRole role)
    {
        var pid = RequirePid();
        var party = await _sessions.SetRoleAsync(code, pid, targetPlayerId, role);
        if (party == null) { await Fail("PartySetRole", "Not allowed."); return; }
        await Clients.Group(PartyGroup(code)).SendAsync(Ev.PartyUpdated, party);
        // Re-push the promoted/demoted player's character view so DM-skill visibility flips immediately
        // (a demotion must revoke access without waiting for the next edit).
        foreach (var dto in await _sessions.CharactersForViewerAsync(code, targetPlayerId))
            await SendToPlayer(targetPlayerId, Ev.CharacterUpdated, dto);
    }

    public async Task PartySetShowHpAp(string code, bool show)
    {
        var pid = RequirePid();
        var party = await _sessions.SetShowHpApAsync(code, pid, show);
        if (party == null) { await Fail("PartySetShowHpAp", "Not allowed."); return; }
        await Clients.Group(PartyGroup(code)).SendAsync(Ev.PartyUpdated, party);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Character + template
    // ═════════════════════════════════════════════════════════════════════════

    public async Task CharacterEditStat(string code, string entityId, string key, int value)
    {
        var pid = RequirePid();
        var ch = await _sessions.EditStatAsync(code, pid, entityId, key, value);
        if (ch != null) await BroadcastCharacterAsync(code, ch);
    }

    public async Task CharacterEditCheck(string code, string entityId, string fieldId, bool value)
    {
        var pid = RequirePid();
        var ch = await _sessions.EditCheckAsync(code, pid, entityId, fieldId, value);
        if (ch != null) await BroadcastCharacterAsync(code, ch);
    }

    public async Task CharacterEditText(string code, string entityId, string fieldId, string value)
    {
        var pid = RequirePid();
        var ch = await _sessions.EditTextAsync(code, pid, entityId, fieldId, value);
        if (ch != null) await BroadcastCharacterAsync(code, ch);
    }

    public async Task CharacterSetSkills(string code, string entityId, List<RpSkill> skills)
    {
        var pid = RequirePid();
        var ch = await _sessions.SetSkillsAsync(code, pid, entityId, skills);
        if (ch != null) await BroadcastCharacterAsync(code, ch);
    }

    public async Task UseSkill(string code, string entityId, Guid skillId)
    {
        var pid = RequirePid();
        var ch = await _sessions.UseSkillAsync(code, pid, entityId, skillId);
        if (ch != null) await BroadcastCharacterAsync(code, ch);
    }

    // ── Entities (companions / NPCs) ──────────────────────────────────────────

    public async Task EntityCreate(string code, EntityKind kind, string name)
    {
        var pid = RequirePid();
        name = InputSanitizer.SanitizeName(name);
        var ch = await _sessions.CreateEntityAsync(code, pid, kind, name);
        if (ch == null) { await Fail("EntityCreate", "Not allowed."); return; }
        await BroadcastCharacterAsync(code, ch);
    }

    public async Task EntityRename(string code, string entityId, string name)
    {
        var pid = RequirePid();
        name = InputSanitizer.SanitizeName(name);
        var ch = await _sessions.RenameEntityAsync(code, pid, entityId, name);
        if (ch == null) { await Fail("EntityRename", "Not allowed."); return; }
        await BroadcastCharacterAsync(code, ch);
    }

    public async Task EntityDelete(string code, string entityId)
    {
        var pid = RequirePid();
        if (await _sessions.DeleteEntityAsync(code, pid, entityId))
            await Clients.Group(PartyGroup(code)).SendAsync(Ev.CharacterRemoved, code, entityId);
        else
            await Fail("EntityDelete", "Not allowed.");
    }

    public async Task EntityImport(string code, string exportCode, EntityKind kind)
    {
        var pid = RequirePid();
        var ch = await _sessions.ImportEntityAsync(code, pid, exportCode, kind);
        if (ch == null) { await Fail("EntityImport", "Could not import — invalid code, not allowed, or entity limit reached."); return; }
        await BroadcastCharacterAsync(code, ch);
    }

    public async Task TemplatePublish(string code, SheetTemplate template)
    {
        var pid = RequirePid();
        var dto = await _sessions.PublishTemplateAsync(code, pid, template);
        if (dto == null) { await Fail("TemplatePublish", "Not allowed."); return; }
        await Clients.Group(PartyGroup(code)).SendAsync(Ev.TemplateUpdated, dto);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dice
    // ═════════════════════════════════════════════════════════════════════════

    public async Task RollDice(string code, string entityId, int die, RollMode mode, string? statFieldId, string? specFieldId)
    {
        var pid = RequirePid();
        var result = await _sessions.RollAsync(code, pid, entityId, Dn, die, mode, statFieldId, specFieldId);
        if (result != null) await Clients.Group(PartyGroup(code)).SendAsync(Ev.DiceRoll, result);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Initiative encounters (joinable, multiple per campaign)
    // ═════════════════════════════════════════════════════════════════════════

    private async Task BroadcastEncounter(string code, EncounterDto? enc)
    {
        if (enc != null) await Clients.Group(PartyGroup(code)).SendAsync(Ev.EncounterUpdated, enc);
    }

    public async Task EncounterCreate(string code, string name)
    {
        var pid = RequirePid();
        var enc = await _sessions.CreateEncounterAsync(code, pid, name);
        if (enc == null) { await Fail("EncounterCreate", "Not allowed."); return; }
        await BroadcastEncounter(code, enc);
    }

    public async Task EncounterDelete(string code, string encounterId)
    {
        var pid = RequirePid();
        if (await _sessions.DeleteEncounterAsync(code, pid, encounterId))
            await Clients.Group(PartyGroup(code)).SendAsync(Ev.EncounterRemoved, code, encounterId);
    }

    public async Task EncounterJoin(string code, string encounterId)
        => await BroadcastEncounter(code, await _sessions.JoinEncounterAsync(code, RequirePid(), encounterId));

    public async Task EncounterLeave(string code, string encounterId)
        => await BroadcastEncounter(code, await _sessions.LeaveEncounterAsync(code, RequirePid(), encounterId));

    public async Task EncounterAddEntity(string code, string encounterId, string entityId)
        => await BroadcastEncounter(code, await _sessions.AddEntityToEncounterAsync(code, RequirePid(), encounterId, entityId));

    public async Task EncounterAddNpc(string code, string encounterId, string name, int roll, int bonus, int hp, int ap)
        => await BroadcastEncounter(code, await _sessions.AddNpcAsync(code, RequirePid(), encounterId, name, roll, bonus, hp, ap));

    public async Task EncounterRemove(string code, string encounterId, string entityId)
        => await BroadcastEncounter(code, await _sessions.RemoveEntryAsync(code, RequirePid(), encounterId, entityId));

    public async Task EncounterEndTurn(string code, string encounterId)
    {
        var pid = RequirePid();
        var res = await _sessions.EndTurnAsync(code, pid, encounterId);
        if (res == null) return;
        // The ending character may have changed from turn-end upkeep (regen, expiring buffs).
        if (res.Value.Ticked != null)
            await BroadcastCharacterAsync(code, res.Value.Ticked);
        await BroadcastEncounter(code, res.Value.State);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Inventory + trading
    // ═════════════════════════════════════════════════════════════════════════

    private async Task BroadcastBag(BagDto bag)
    {
        foreach (var participant in bag.ParticipantIds)
            await SendToPlayer(participant, Ev.BagUpdated, bag);
    }

    public async Task BagCreate(string code, string name, bool isDmBag)
    {
        var pid = RequirePid();
        var bag = await _sessions.CreateBagAsync(code, pid, name, isDmBag);
        if (bag != null) await BroadcastBag(bag);
    }

    public async Task BagRename(Guid bagId, string name)
    {
        var bag = await _sessions.RenameBagAsync(bagId, RequirePid(), name);
        if (bag != null) await BroadcastBag(bag);
    }

    // `path` is the chain of bag-item ids to descend into a nested bag (empty = the inventory root).
    public async Task ItemAdd(Guid bagId, Guid[] path, RpItemDto item)
    {
        var bag = await _sessions.AddItemAsync(bagId, RequirePid(), path ?? Array.Empty<Guid>(), item);
        if (bag != null) await BroadcastBag(bag);
    }

    public async Task ItemRemove(Guid bagId, Guid[] path, Guid itemId)
    {
        var bag = await _sessions.RemoveItemAsync(bagId, RequirePid(), path ?? Array.Empty<Guid>(), itemId);
        if (bag != null) await BroadcastBag(bag);
    }

    public async Task ItemUpdate(Guid bagId, Guid[] path, RpItemDto item)
    {
        var bag = await _sessions.UpdateItemAsync(bagId, RequirePid(), path ?? Array.Empty<Guid>(), item);
        if (bag != null) await BroadcastBag(bag);
    }

    public async Task ItemMove(Guid fromBag, Guid[] fromPath, Guid toBag, Guid[] toPath, Guid itemId)
    {
        var res = await _sessions.MoveItemAsync(
            fromBag, fromPath ?? Array.Empty<Guid>(), toBag, toPath ?? Array.Empty<Guid>(), RequirePid(), itemId);
        if (res != null) { await BroadcastBag(res.Value.From); await BroadcastBag(res.Value.To); }
    }

    public async Task BagDelete(Guid bagId)
    {
        var res = await _sessions.DeleteBagAsync(bagId, RequirePid());
        if (res == null) { await Fail("BagDelete", "Not allowed."); return; }
        foreach (var participant in res.Value.Participants)
            await SendToPlayer(participant, Ev.BagRemoved, bagId);
    }

    public async Task BagShareInvite(Guid bagId, string toPlayerId)
    {
        var pid = RequirePid();
        var invite = await _sessions.InviteToBagAsync(bagId, pid, Dn, toPlayerId);
        if (invite != null) await SendToPlayer(toPlayerId, Ev.BagShareInvited, invite);
    }

    public async Task BagShareAccept(Guid bagId)
    {
        var bag = await _sessions.AcceptBagShareAsync(bagId, RequirePid());
        if (bag != null) await BroadcastBag(bag);
    }

    public async Task BagShareDecline(Guid bagId)
    {
        var pid  = RequirePid();
        var info = await _sessions.DeclineBagShareAsync(bagId, pid);
        if (info != null)
            await SendToPlayer(info.Value.OwnerId, Ev.BagShareDeclined,
                new BagShareDeclinedDto(bagId, info.Value.BagName, pid, Dn));
    }

    public async Task BagLeave(Guid bagId)
    {
        var pid = RequirePid();
        var bag = await _sessions.LeaveBagAsync(bagId, pid);
        if (bag != null) { await BroadcastBag(bag); await SendToPlayer(pid, Ev.BagRemoved, bagId); }
    }

    public async Task ItemSplit(Guid bagId, Guid[] path, Guid itemId, int amount)
    {
        var bag = await _sessions.SplitItemAsync(bagId, RequirePid(), path ?? Array.Empty<Guid>(), itemId, amount);
        if (bag != null) await BroadcastBag(bag);
    }

    public async Task UseItem(Guid bagId, Guid[] path, Guid itemId)
    {
        var res = await _sessions.UseItemAsync(bagId, RequirePid(), path ?? Array.Empty<Guid>(), itemId);
        if (res == null) return;
        await BroadcastCharacterAsync(res.Value.Character.PartyCode, res.Value.Character);
        await BroadcastBag(res.Value.Bag);
    }

    public async Task EquipItem(Guid bagId, Guid[] path, Guid itemId)
    {
        var res = await _sessions.EquipItemAsync(bagId, RequirePid(), path ?? Array.Empty<Guid>(), itemId);
        if (res == null) return;
        await BroadcastCharacterAsync(res.Value.Character.PartyCode, res.Value.Character);
        await BroadcastBag(res.Value.Bag);
    }

    public async Task UnequipItem(string code, RpItemType slot, Guid toBagId)
    {
        var res = await _sessions.UnequipItemAsync(code, RequirePid(), slot, toBagId);
        if (res == null) return;
        await BroadcastCharacterAsync(res.Value.Character.PartyCode, res.Value.Character);
        await BroadcastBag(res.Value.Bag);
    }

    public async Task TradeOffer(string code, string toPlayerId, Guid sourceBag, Guid[] sourcePath, Guid itemId, int amount, bool isCopy)
    {
        var pid = RequirePid();
        if (!await _sessions.IsMemberAsync(code, toPlayerId)) { await Fail("TradeOffer", "Target not in campaign."); return; }
        var offer = await _sessions.CreateTradeAsync(code, pid, Dn, toPlayerId, sourceBag, sourcePath ?? Array.Empty<Guid>(), itemId, amount, isCopy);
        if (offer == null) { await Fail("TradeOffer", "Couldn't offer that item."); return; }
        await SendToPlayer(toPlayerId, Ev.TradeOffered,
            new TradeOfferDto(offer.OfferId, offer.FromPlayerId, offer.FromDisplayName, offer.ToPlayerId, offer.Item, offer.IsCopy));
    }

    public async Task TradeAccept(Guid offerId)
    {
        var bags = await _sessions.AcceptTradeAsync(offerId, RequirePid());
        foreach (var bag in bags) await BroadcastBag(bag);
    }

    public Task TradeDecline(Guid offerId) { _sessions.TakeTrade(offerId, RequirePid()); return Task.CompletedTask; }

    // ═════════════════════════════════════════════════════════════════════════
    // BGM rooms
    // ═════════════════════════════════════════════════════════════════════════

    private static string RoomGroup(string code) => "room:" + code;

    public async Task RoomCreate(string name, string? password, string? campaignCode)
    {
        var pid = RequirePid();
        var room = await _sessions.CreateRoomAsync(pid, name, password, campaignCode);
        if (room == null) { await Fail("RoomCreate", "Could not create room."); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(room.Code));
        await Clients.Caller.SendAsync(Ev.RoomUpdated, room);
        // A campaign room is announced to the whole campaign so every member sees it without reconnecting.
        if (!string.IsNullOrEmpty(room.CampaignCode))
            await Clients.OthersInGroup(PartyGroup(room.CampaignCode)).SendAsync(Ev.RoomUpdated, room);
    }

    public async Task RoomJoin(string code, string? password)
    {
        var pid = RequirePid();
        var room = await _sessions.JoinRoomAsync(code, pid, password);
        if (room == null) { await Fail("RoomJoin", "No such room, or wrong password."); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(code));
        await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
    }

    public async Task RoomLeave(string code)
    {
        var pid = RequirePid();
        var (room, removed) = await _sessions.LeaveRoomAsync(code, pid);
        if (removed) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomRemoved, code);
        else if (room != null) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(code));
    }

    public async Task RoomPromote(string code, string targetPlayerId, RoomRole role)
    {
        var pid = RequirePid();
        var room = await _sessions.PromoteRoomAsync(code, pid, targetPlayerId, role);
        if (room != null) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
    }

    public async Task PlaylistAdd(string code, string title, string url)
    {
        var room = await _sessions.PlaylistAddAsync(code, RequirePid(), title, url);
        if (room != null) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
    }

    public async Task PlaylistRemove(string code, Guid songId)
    {
        var room = await _sessions.PlaylistRemoveAsync(code, RequirePid(), songId);
        if (room != null) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
    }

    public async Task PlaybackCommand(string code, PlaybackCommandDto cmd)
    {
        var room = await _sessions.PlaybackCommandAsync(code, RequirePid(), cmd);
        if (room != null) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
    }

    /// <summary>A client has the cued song ready. May commit the "waiting for members" gate (synchronized start).</summary>
    public async Task BgmReady(string code, int songIndex, long version)
    {
        var room = await _sessions.MarkReadyAsync(code, RequirePid(), songIndex, version);
        if (room != null) await Clients.Group(RoomGroup(code)).SendAsync(Ev.RoomUpdated, room);
    }

    /// <summary>Declares which BGM room this connection is actively listening to ("" = none). Drives the gate's expected set.</summary>
    public Task BgmSetActive(string code)
    {
        _sessions.SetActiveRoom(Context.ConnectionId, code);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mints a short-lived signed URL for the server-side WAV of a YouTube video id. Requires an
    /// identified caller, so the audio endpoint can't be driven as an open proxy. Returns null for a
    /// bad id. The client prepends the server base URL and GETs the WAV.
    /// </summary>
    public Task<string?> ResolveBgmAudio(string videoId)
    {
        RequirePid();
        if (!BgmAudioService.IsValidVideoId(videoId)) return Task.FromResult<string?>(null);
        return Task.FromResult<string?>(_audio.BuildSignedPath(videoId, TimeSpan.FromHours(6)));
    }
}
