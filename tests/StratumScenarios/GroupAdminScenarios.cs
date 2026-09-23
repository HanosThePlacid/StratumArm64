using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Xunit;

namespace StratumScenarios;

/// <summary>
/// Issue #335: staff authority over player groups. The seeded stratum.json defines three kinds
/// so every rule under test has something to bite on: "faction" is exclusive, "squad" caps at
/// one member, and "utility" does not count as the same side for friendly fire. Kinds cannot be
/// set through /stratum set (they are a list), so they arrive through the fixture, seeded at the
/// current config version so the boot loads it without running a migration.
///
/// Every scenario drives real commands: group creation and invites run as the player through the
/// command API with an explicit Caller, staff actions run as the console. The class shares one
/// server boot, so each scenario uses its own group and player names.
/// </summary>
[AtlasDataFiles("fixtures/stratum-groups", TargetPath = "")]
public class GroupAdminScenarios : AtlasScenarioBase
{
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task PlayerJoin_Should_BeRefused_When_AlreadyInAnotherFaction()
	{
		ITestPlayer red = await World.JoinPlayer("grp-red-owner");
		ITestPlayer blue = await World.JoinPlayer("grp-blue-owner");
		ITestPlayer recruit = await World.JoinPlayer("grp-recruit");

		await CreateGroup(red, "RedOne");
		await CreateGroup(blue, "BlueOne");
		await SetKind("RedOne", "faction");
		await SetKind("BlueOne", "faction");

		// First faction is fine.
		await Invite(red, "RedOne", recruit);
		TextCommandResult joinedRed = await ExecuteAs(recruit, "/group acceptinvite RedOne");
		Assert.Equal(EnumCommandStatus.Success, joinedRed.Status);

		// Second one is not, because the kind is exclusive.
		await Invite(blue, "BlueOne", recruit);
		TextCommandResult joinedBlue = await ExecuteAs(recruit, "/group acceptinvite BlueOne");
		Assert.Equal(EnumCommandStatus.Error, joinedBlue.Status);
		Assert.Contains("RedOne", joinedBlue.StatusMessage);

		// A non-exclusive kind alongside it still works, which is the whole point of kinds:
		// only factions are one-at-a-time, administrative groupings are not.
		await CreateGroup(red, "HelpDesk");
		await SetKind("HelpDesk", "utility");
		await Invite(red, "HelpDesk", recruit);
		TextCommandResult joinedUtility = await ExecuteAs(recruit, "/group acceptinvite HelpDesk");
		Assert.Equal(EnumCommandStatus.Success, joinedUtility.Status);

		await Leave(red, blue, recruit);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task StaffAdd_Should_MovePlayer_When_FactionConflicts()
	{
		// Two owners, not one: /group admin kind refuses to stamp an exclusive kind on a group
		// whose members already hold another group of that kind, and a single owner of both
		// groups would trip that rule before this scenario got to the part it is about.
		ITestPlayer redOwner = await World.JoinPlayer("grp-move-red");
		ITestPlayer blueOwner = await World.JoinPlayer("grp-move-blue");
		ITestPlayer moved = await World.JoinPlayer("grp-moved");

		await CreateGroup(redOwner, "RedTwo");
		await CreateGroup(blueOwner, "BlueTwo");
		await SetKind("RedTwo", "faction");
		await SetKind("BlueTwo", "faction");

		CommandResult intoRed = await World.ExecuteCommand($"/group admin add RedTwo {moved.Player.PlayerName}");
		Assert.True(intoRed.Ok, intoRed.Message);

		// A player join would be refused here. A staff add reassigns instead, because that is
		// what "put this player on blue" means during an event.
		CommandResult intoBlue = await World.ExecuteCommand($"/group admin add BlueTwo {moved.Player.PlayerName}");
		Assert.True(intoBlue.Ok, intoBlue.Message);
		Assert.Contains("RedTwo", intoBlue.Message);

		CommandResult redInfo = await World.ExecuteCommand("/group admin info RedTwo");
		Assert.Contains("Members", redInfo.Message);
		Assert.True(await MemberCount("RedTwo") == 1, "RedTwo should be down to its owner");
		Assert.True(await MemberCount("BlueTwo") == 2, "BlueTwo should hold its owner and the moved player");

		await Leave(redOwner, blueOwner, moved);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Leave_Should_BeRefused_When_MemberLocked()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-lock-owner");
		ITestPlayer member = await World.JoinPlayer("grp-locked");

		await CreateGroup(owner, "LockedSquad");
		await SetKind("LockedSquad", "faction");
		await StaffAdd("LockedSquad", member);

		CommandResult locked = await World.ExecuteCommand($"/group admin lock LockedSquad {member.Player.PlayerName}");
		Assert.True(locked.Ok, locked.Message);

		TextCommandResult leaveWhileLocked = await ExecuteAs(member, "/group leave LockedSquad");
		Assert.Equal(EnumCommandStatus.Error, leaveWhileLocked.Status);
		Assert.Contains("locked", leaveWhileLocked.StatusMessage);

		CommandResult unlocked = await World.ExecuteCommand($"/group admin unlock LockedSquad {member.Player.PlayerName}");
		Assert.True(unlocked.Ok, unlocked.Message);

		TextCommandResult leaveAfterUnlock = await ExecuteAs(member, "/group leave LockedSquad");
		Assert.Equal(EnumCommandStatus.Success, leaveAfterUnlock.Status);

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Lock_Should_Expire_When_DurationPasses()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-expiry-owner");
		ITestPlayer member = await World.JoinPlayer("grp-expiring");

		await CreateGroup(owner, "TimedSquad");
		await SetKind("TimedSquad", "faction");
		await StaffAdd("TimedSquad", member);

		CommandResult locked = await World.ExecuteCommand($"/group admin lock TimedSquad {member.Player.PlayerName} 3s");
		Assert.True(locked.Ok, locked.Message);

		TextCommandResult duringLock = await ExecuteAs(member, "/group leave TimedSquad");
		Assert.Equal(EnumCommandStatus.Error, duringLock.Status);

		// The expiry is wall-clock, not tick-driven: nothing sweeps locks, they lapse the next
		// time one is read. Ticking here only keeps the server busy while it passes.
		await Task.Delay(TimeSpan.FromSeconds(4));
		await World.Ticks(10);

		TextCommandResult afterExpiry = await ExecuteAs(member, "/group leave TimedSquad");
		Assert.Equal(EnumCommandStatus.Success, afterExpiry.Status);

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Kick_Should_BeRefused_When_MemberLocked()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-kick-owner");
		ITestPlayer member = await World.JoinPlayer("grp-kick-target");

		await CreateGroup(owner, "KickSquad");
		await SetKind("KickSquad", "faction");
		await StaffAdd("KickSquad", member);
		CommandResult locked = await World.ExecuteCommand($"/group admin lock KickSquad {member.Player.PlayerName}");
		Assert.True(locked.Ok, locked.Message);

		// Without this gate the group owner could void a staff lock from the group UI.
		TextCommandResult kick = await ExecuteAs(owner, $"/group kick KickSquad {member.Player.PlayerName}");
		Assert.Equal(EnumCommandStatus.Error, kick.Status);
		Assert.Contains("locked", kick.StatusMessage);

		// Staff removal is deliberate, so it clears the lock instead of tripping over it.
		CommandResult staffRemove = await World.ExecuteCommand($"/group admin remove KickSquad {member.Player.PlayerName}");
		Assert.True(staffRemove.Ok, staffRemove.Message);
		Assert.True(await MemberCount("KickSquad") == 1, "only the owner should remain");

		await Leave(owner, member);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task FrozenRoster_Should_BlockInviteLeaveAndDisband()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-freeze-owner");
		ITestPlayer member = await World.JoinPlayer("grp-freeze-mem");
		ITestPlayer outsider = await World.JoinPlayer("grp-freeze-out");

		await CreateGroup(owner, "FrozenTeam");
		await StaffAdd("FrozenTeam", member);

		CommandResult frozen = await World.ExecuteCommand("/group admin freeze FrozenTeam on");
		Assert.True(frozen.Ok, frozen.Message);

		TextCommandResult invite = await ExecuteAs(owner, $"/group invite FrozenTeam {outsider.Player.PlayerName}");
		Assert.Equal(EnumCommandStatus.Error, invite.Status);

		TextCommandResult leave = await ExecuteAs(member, "/group leave FrozenTeam");
		Assert.Equal(EnumCommandStatus.Error, leave.Status);

		TextCommandResult disband = await ExecuteAs(owner, "/group disband FrozenTeam");
		Assert.Equal(EnumCommandStatus.Error, disband.Status);

		CommandResult thawed = await World.ExecuteCommand("/group admin freeze FrozenTeam off");
		Assert.True(thawed.Ok, thawed.Message);

		TextCommandResult leaveAfterThaw = await ExecuteAs(member, "/group leave FrozenTeam");
		Assert.Equal(EnumCommandStatus.Success, leaveAfterThaw.Status);

		await Leave(owner, member, outsider);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Join_Should_BeRefused_When_KindMemberCapReached()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-cap-owner");
		ITestPlayer recruit = await World.JoinPlayer("grp-cap-recruit");

		// The squad kind caps at one member, and /group create leaves the owner in it.
		await CreateGroup(owner, "OneSeat");
		await SetKind("OneSeat", "squad");

		await Invite(owner, "OneSeat", recruit);
		TextCommandResult accept = await ExecuteAs(recruit, "/group acceptinvite OneSeat");
		Assert.Equal(EnumCommandStatus.Error, accept.Status);
		Assert.Contains("full", accept.StatusMessage);

		// Staff go past the cap deliberately; the audit log records that they did.
		CommandResult staffAdd = await World.ExecuteCommand($"/group admin add OneSeat {recruit.Player.PlayerName}");
		Assert.True(staffAdd.Ok, staffAdd.Message);
		Assert.True(await MemberCount("OneSeat") == 2, "the staff add should have gone through");

		await Leave(owner, recruit);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task SameSide_Should_FollowKindAndRelations()
	{
		ITestPlayer first = await World.JoinPlayer("grp-side-a");
		ITestPlayer second = await World.JoinPlayer("grp-side-b");

		// A shared group of a counting kind: same side, so friendly fire is off between them.
		await CreateGroup(first, "SideFaction");
		await SetKind("SideFaction", "faction");
		await StaffAdd("SideFaction", second);
		await World.Ticks(5);
		Assert.True(StratumPlayerGroups.SharesGroup(first.Player, second.Player));

		// The same two players sharing only an administrative group are not. Before kinds, an
		// admin group like this silently switched PvP off between everyone in it.
		CommandResult leaveFirst = await World.ExecuteCommand($"/group admin remove SideFaction {first.Player.PlayerName}");
		Assert.True(leaveFirst.Ok, leaveFirst.Message);
		CommandResult leaveSecond = await World.ExecuteCommand($"/group admin remove SideFaction {second.Player.PlayerName}");
		Assert.True(leaveSecond.Ok, leaveSecond.Message);

		await CreateGroup(first, "SideDesk");
		await SetKind("SideDesk", "utility");
		await StaffAdd("SideDesk", second);
		await World.Ticks(5);
		Assert.False(StratumPlayerGroups.SharesGroup(first.Player, second.Player));
		await Leave(first, second);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task AlliedFactions_Should_CountAsSameSide()
	{
		ITestPlayer first = await World.JoinPlayer("grp-ally-a");
		ITestPlayer second = await World.JoinPlayer("grp-ally-b");

		await CreateGroup(first, "AllyOne");
		await CreateGroup(second, "AllyTwo");
		await SetKind("AllyOne", "faction");
		await SetKind("AllyTwo", "faction");
		await World.Ticks(5);
		Assert.False(StratumPlayerGroups.SharesGroup(first.Player, second.Player));

		CommandResult allied = await World.ExecuteCommand("/group admin relation AllyOne AllyTwo ally");
		Assert.True(allied.Ok, allied.Message);
		Assert.True(StratumPlayerGroups.SharesGroup(first.Player, second.Player));

		// The relation is written to both sides, so it reads the same from either group.
		CommandResult fromOther = await World.ExecuteCommand("/group admin info AllyTwo");
		Assert.Contains("AllyOne", fromOther.Message);

		CommandResult enemies = await World.ExecuteCommand("/group admin relation AllyOne AllyTwo enemy");
		Assert.True(enemies.Ok, enemies.Message);
		Assert.False(StratumPlayerGroups.SharesGroup(first.Player, second.Player));

		await Leave(first, second);
	}

	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task Nametag_Should_CarryGroupTag_When_TagSet()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-tag-owner");

		await CreateGroup(owner, "TaggedTeam");
		await SetKind("TaggedTeam", "faction");

		CommandResult tagged = await World.ExecuteCommand("/group admin tag TaggedTeam RED");
		Assert.True(tagged.Ok, tagged.Message);
		await World.Ticks(5);

		Assert.Contains("[RED]", NametagOf(owner));

		CommandResult cleared = await World.ExecuteCommand("/group admin tag TaggedTeam none");
		Assert.True(cleared.Ok, cleared.Message);
		await World.Ticks(5);

		Assert.DoesNotContain("[RED]", NametagOf(owner));

		await Leave(owner);
	}

	/// <summary>
	/// /group info is the player-facing half. A player who is not staff has to be able to read
	/// the state that governs them: which kind the group is, its tag, whether the roster is
	/// frozen, who it is allied with or at war with, and whether they are personally locked in.
	/// </summary>
	[AtlasScenario(TimeoutMs = 300_000)]
	public async Task GroupInfo_Should_ShowStateToPlayers()
	{
		ITestPlayer owner = await World.JoinPlayer("grp-info-own");
		ITestPlayer other = await World.JoinPlayer("grp-info-oth");

		await CreateGroup(owner, "InfoRed");
		await CreateGroup(other, "InfoBlue");
		await SetKind("InfoRed", "faction");
		await SetKind("InfoBlue", "faction");

		CommandResult tagged = await World.ExecuteCommand("/group admin tag InfoRed RED");
		Assert.True(tagged.Ok, tagged.Message);
		CommandResult related = await World.ExecuteCommand("/group admin relation InfoRed InfoBlue enemy");
		Assert.True(related.Ok, related.Message);
		CommandResult locked = await World.ExecuteCommand($"/group admin lock InfoRed {owner.Player.PlayerName}");
		Assert.True(locked.Ok, locked.Message);

		// The owner holds no staff privilege here, so this is the ordinary player's view.
		TextCommandResult mine = await ExecuteAs(owner, "/group info InfoRed");
		Assert.Equal(EnumCommandStatus.Success, mine.Status);
		Assert.Contains("faction", mine.StatusMessage);
		Assert.Contains("[RED]", mine.StatusMessage);
		Assert.Contains("InfoBlue", mine.StatusMessage);
		Assert.Contains("enemy", mine.StatusMessage);
		Assert.Contains("locked", mine.StatusMessage);

		// Someone outside the group reads the same public state, but not another player's lock.
		TextCommandResult theirs = await ExecuteAs(other, "/group info InfoRed");
		Assert.Equal(EnumCommandStatus.Success, theirs.Status);
		Assert.Contains("InfoBlue", theirs.StatusMessage);
		Assert.DoesNotContain("Your membership", theirs.StatusMessage);

		CommandResult frozen = await World.ExecuteCommand("/group admin freeze InfoRed on");
		Assert.True(frozen.Ok, frozen.Message);

		TextCommandResult afterFreeze = await ExecuteAs(owner, "/group info InfoRed");
		Assert.Contains("frozen", afterFreeze.StatusMessage);

		// A group that was never touched by /group admin still reads exactly as vanilla did.
		await CreateGroup(other, "InfoPlain");
		TextCommandResult plain = await ExecuteAs(other, "/group info InfoPlain");
		Assert.Equal(EnumCommandStatus.Success, plain.Status);
		Assert.Contains("Members", plain.StatusMessage);
		Assert.DoesNotContain("Kind", plain.StatusMessage);
		Assert.DoesNotContain("Roster", plain.StatusMessage);
		Assert.DoesNotContain("Relations", plain.StatusMessage);

		await Leave(owner, other);
	}

	// ---------------------------------------------------------------- helpers

	/// <summary>
	/// Frees the slots again. The class shares one server boot and the world caps at 16
	/// players, so scenarios that each join two or three would run the server out of room
	/// part-way through the suite.
	/// </summary>
	private async Task Leave(params ITestPlayer[] players)
	{
		foreach (ITestPlayer player in players)
		{
			player.Player.Disconnect();
		}

		foreach (ITestPlayer player in players)
		{
			await World.Until(() => !player.IsConnected, timeoutTicks: 600);
		}

		await World.Ticks(5);
	}

	private static string NametagOf(ITestPlayer player)
	{
		return player.Player.Entity?.WatchedAttributes.GetTreeAttribute("nametag")?.GetString("name") ?? string.Empty;
	}

	private async Task CreateGroup(ITestPlayer owner, string groupName)
	{
		TextCommandResult result = await ExecuteAs(owner, $"/group create {groupName}");
		Assert.Equal(EnumCommandStatus.Success, result.Status);
	}

	private async Task SetKind(string groupName, string kind)
	{
		CommandResult result = await World.ExecuteCommand($"/group admin kind {groupName} {kind}");
		Assert.True(result.Ok, result.Message);
	}

	private async Task StaffAdd(string groupName, ITestPlayer player)
	{
		CommandResult result = await World.ExecuteCommand($"/group admin add {groupName} {player.Player.PlayerName}");
		Assert.True(result.Ok, result.Message);
	}

	private async Task Invite(ITestPlayer inviter, string groupName, ITestPlayer target)
	{
		TextCommandResult result = await ExecuteAs(inviter, $"/group invite {groupName} {target.Player.PlayerName}");
		Assert.Equal(EnumCommandStatus.Success, result.Status);
	}

	/// <summary>Member count as /group admin info reports it, parsed off the "Members" row.</summary>
	private async Task<int> MemberCount(string groupName)
	{
		CommandResult info = await World.ExecuteCommand($"/group admin info {groupName}");
		Assert.True(info.Ok, info.Message);

		string message = info.Message ?? string.Empty;
		int rowStart = message.IndexOf("Members", StringComparison.Ordinal);
		Assert.True(rowStart >= 0, $"no Members row in: {message}");

		int digitStart = -1;
		for (int index = rowStart; index < message.Length; index++)
		{
			if (char.IsDigit(message[index]))
			{
				digitStart = index;
				break;
			}
		}

		Assert.True(digitStart >= 0, $"no member count in: {message}");
		int digitEnd = digitStart;
		while (digitEnd < message.Length && char.IsDigit(message[digitEnd]))
		{
			digitEnd++;
		}

		return int.Parse(message[digitStart..digitEnd]);
	}

	/// <summary>
	/// Runs a command as the player rather than the console. /group leans on the caller for
	/// ownership, group privileges and the chat group it was typed in, so the console's
	/// all-privileges caller would prove nothing here.
	/// </summary>
	private Task<TextCommandResult> ExecuteAs(ITestPlayer player, string command)
	{
		var completion = new TaskCompletionSource<TextCommandResult>();
		World.Api.ChatCommands.ExecuteUnparsed(
			command,
			new TextCommandCallingArgs
			{
				Caller = new Caller
				{
					Type = EnumCallerType.Player,
					Player = player.Player,
					FromChatGroupId = GlobalConstants.GeneralChatGroup,
				},
			},
			result => completion.TrySetResult(result));
		return completion.Task;
	}
}
