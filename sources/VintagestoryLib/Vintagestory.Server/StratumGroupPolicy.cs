using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Vintagestory.Server;

/// <summary>
/// Stratum #335: the rules behind staff group administration. Every gate the vanilla group
/// command calls lives here, so the vanilla file keeps one-line calls and this class owns the
/// "may this happen" decisions: roster freeze, membership locks, kind exclusivity, member caps,
/// group relations and group tags.
///
/// Group state (kind, freeze, tag, relations) is stored on <see cref="PlayerGroup"/> itself and
/// persists to playergroups.json. Per-player locks live in the player's CustomPlayerData, which
/// is where a lock belongs: it follows the player, works while they are offline, and disappears
/// with them.
/// </summary>
internal static class StratumGroupPolicy
{
	public const string RelationAlly = "ally";

	public const string RelationEnemy = "enemy";

	public const string RelationNeutral = "neutral";

	private const string LocksKey = "stratum.group-locks.v1";

	private static ServerMain installedServer;

	private static StratumGroupsConfig Config
	{
		get
		{
			StratumConfig config = StratumRuntime.Config;
			config.EnsurePopulated();
			return config.Groups;
		}
	}

	public static bool Enabled => Config.Enabled;

	/// <summary>
	/// Points the API-level friendly fire predicate at this server's group state. Without it
	/// <see cref="StratumPlayerGroups.SharesGroup(IPlayer, IPlayer)"/> keeps its standalone
	/// behaviour of counting every shared group, which is what vanilla Stratum did before #335.
	/// </summary>
	public static void Install(ServerMain server)
	{
		installedServer = server;
		StratumPlayerGroups.StratumGroupCountsAsSameSide = GroupCountsAsSameSide;
		StratumPlayerGroups.StratumGroupsAreAllied = GroupsAreAllied;
	}

	// ---------------------------------------------------------------- kinds

	public static StratumGroupKindConfig KindOf(PlayerGroup group)
	{
		return KindByCode(group?.StratumKind);
	}

	public static StratumGroupKindConfig KindByCode(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return StratumGroupKindConfig.Unclassified;
		}

		List<StratumGroupKindConfig> kinds = Config.Kinds;
		for (int index = 0; index < kinds.Count; index++)
		{
			if (string.Equals(kinds[index].Code, code, StringComparison.OrdinalIgnoreCase))
			{
				return kinds[index];
			}
		}

		// A group tagged with a kind the config no longer defines falls back to unclassified
		// rather than locking every member out of a group they cannot leave.
		return StratumGroupKindConfig.Unclassified;
	}

	public static bool KindExists(string code)
	{
		return Config.Kinds.Any(kind => string.Equals(kind.Code, code, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>Called for every group made with /group create.</summary>
	public static void OnGroupCreated(ServerMain server, PlayerGroup group)
	{
		if (group == null || !Enabled)
		{
			return;
		}

		string defaultKind = Config.DefaultKind;
		if (defaultKind == null)
		{
			return;
		}

		StratumGroupKindConfig kind = KindByCode(defaultKind);
		if (kind.Code == null)
		{
			StratumRuntime.LogWarning("groups: DefaultKind '" + defaultKind + "' is not defined in Groups.Kinds, new groups stay unclassified");
			return;
		}

		if (!kind.PlayerCreatable)
		{
			StratumRuntime.LogWarning("groups: DefaultKind '" + kind.Code + "' is not PlayerCreatable, new groups stay unclassified");
			return;
		}

		group.StratumKind = kind.Code;
		server.PlayerDataManager.playerGroupsDirty = true;
	}

	// ---------------------------------------------------------------- player-initiated gates

	/// <summary>
	/// Gate for a player joining a group themselves, through /group join or by accepting an
	/// invite. Refuses rather than reassigning: a player picking a second faction is a mistake,
	/// not an instruction.
	/// </summary>
	public static bool TryPlayerJoin(ServerMain server, ServerPlayerData playerData, PlayerGroup group, out string error)
	{
		error = null;
		if (!Enabled || group == null || playerData == null)
		{
			return true;
		}

		if (group.StratumRosterFrozen)
		{
			error = "The roster of group " + group.Name + " is frozen.";
			return false;
		}

		StratumGroupKindConfig kind = KindOf(group);
		if (kind.Exclusive)
		{
			PlayerGroup conflict = FindExclusiveConflict(server, playerData, kind, group.Uid);
			if (conflict != null)
			{
				error = "You are already in " + conflict.Name + ", and a player can only be in one " + kind.Code + " at a time.";
				return false;
			}
		}

		if (kind.MaxMembers > 0 && CountMembers(server, group.Uid) >= kind.MaxMembers)
		{
			error = "Group " + group.Name + " is full (" + kind.MaxMembers + " members).";
			return false;
		}

		return true;
	}

	/// <summary>Gate for /group leave.</summary>
	public static bool TryPlayerLeave(ServerMain server, ServerPlayerData playerData, PlayerGroup group, out string error)
	{
		error = null;
		if (!Enabled || group == null || playerData == null)
		{
			return true;
		}

		if (IsLocked(server, playerData, group.Uid, out DateTime? expires))
		{
			error = "You are locked into group " + group.Name + FormatUntil(expires) + ".";
			return false;
		}

		if (group.StratumRosterFrozen)
		{
			error = "The roster of group " + group.Name + " is frozen.";
			return false;
		}

		return true;
	}

	/// <summary>Gate for a group op kicking a member, which would otherwise void a lock.</summary>
	public static bool TryPlayerKick(ServerMain server, ServerPlayerData targetData, PlayerGroup group, out string error)
	{
		error = null;
		if (!Enabled || group == null || targetData == null)
		{
			return true;
		}

		if (IsLocked(server, targetData, group.Uid, out DateTime? expires))
		{
			error = targetData.LastKnownPlayername + " is locked into group " + group.Name + FormatUntil(expires) + ".";
			return false;
		}

		if (group.StratumRosterFrozen)
		{
			error = "The roster of group " + group.Name + " is frozen.";
			return false;
		}

		return true;
	}

	/// <summary>Gate for /group invite and /group disband, both of which move the roster.</summary>
	public static bool TryRosterChange(PlayerGroup group, string action, out string error)
	{
		error = null;
		if (!Enabled || group == null || !group.StratumRosterFrozen)
		{
			return true;
		}

		error = "The roster of group " + group.Name + " is frozen, so you cannot " + action + ".";
		return false;
	}

	// ---------------------------------------------------------------- staff gates

	/// <summary>
	/// Gate for a staff add (/group admin add, and vanilla /group addplayer). Unlike a player
	/// join this reassigns on an exclusive conflict, because that is what an event admin means
	/// by "put this player on red team". The displaced group comes back so the caller can
	/// report it. A lock on the displaced group still wins: staff clear the lock first.
	/// </summary>
	public static bool TryStaffAdd(ServerMain server, ServerPlayerData playerData, PlayerGroup group, out PlayerGroup displaced, out string error)
	{
		displaced = null;
		error = null;
		if (!Enabled || group == null || playerData == null)
		{
			return true;
		}

		StratumGroupKindConfig kind = KindOf(group);
		if (kind.Exclusive)
		{
			PlayerGroup conflict = FindExclusiveConflict(server, playerData, kind, group.Uid);
			if (conflict != null)
			{
				if (IsLocked(server, playerData, conflict.Uid, out DateTime? expires))
				{
					error = playerData.LastKnownPlayername + " is locked into " + conflict.Name + FormatUntil(expires) + ". Unlock them first.";
					return false;
				}

				displaced = conflict;
			}
		}

		// The member cap is a player-facing limit. Staff go past it deliberately, so this only
		// notes it in the audit log rather than refusing.
		if (kind.MaxMembers > 0 && CountMembers(server, group.Uid) >= kind.MaxMembers)
		{
			LogAudit("group admin add over cap group=" + group.Name + " cap=" + kind.MaxMembers + " player=" + playerData.LastKnownPlayername);
		}

		return true;
	}

	private static PlayerGroup FindExclusiveConflict(ServerMain server, ServerPlayerData playerData, StratumGroupKindConfig kind, int joiningUid)
	{
		if (playerData.PlayerGroupMemberShips == null)
		{
			return null;
		}

		foreach (KeyValuePair<int, PlayerGroupMembership> membership in playerData.PlayerGroupMemberShips)
		{
			if (membership.Key == joiningUid || membership.Value == null || membership.Value.Level == EnumPlayerGroupMemberShip.None)
			{
				continue;
			}

			if (!server.PlayerDataManager.PlayerGroupsById.TryGetValue(membership.Key, out PlayerGroup other))
			{
				continue;
			}

			if (string.Equals(other.StratumKind, kind.Code, StringComparison.OrdinalIgnoreCase))
			{
				return other;
			}
		}

		return null;
	}

	public static int CountMembers(ServerMain server, int groupUid)
	{
		int count = 0;
		foreach (ServerPlayerData data in server.PlayerDataManager.PlayerDataByUid.Values)
		{
			if (data.PlayerGroupMemberShips != null
				&& data.PlayerGroupMemberShips.TryGetValue(groupUid, out PlayerGroupMembership membership)
				&& membership != null
				&& membership.Level != EnumPlayerGroupMemberShip.None)
			{
				count++;
			}
		}

		return count;
	}

	// ---------------------------------------------------------------- locks

	public static bool IsLocked(ServerMain server, ServerPlayerData playerData, int groupUid)
	{
		return IsLocked(server, playerData, groupUid, out _);
	}

	public static bool IsLocked(ServerMain server, ServerPlayerData playerData, int groupUid, out DateTime? expiresUtc)
	{
		expiresUtc = null;
		if (!Enabled || playerData == null)
		{
			return false;
		}

		Dictionary<int, long> locks = ReadLocks(playerData);
		if (locks == null || !locks.TryGetValue(groupUid, out long expiry))
		{
			return false;
		}

		if (expiry > 0 && expiry <= NowMs())
		{
			locks.Remove(groupUid);
			WriteLocks(server, playerData, locks);
			return false;
		}

		expiresUtc = expiry > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expiry).UtcDateTime : null;
		return true;
	}

	/// <summary>Locks a player into a group. Null expiry means until staff unlock it.</summary>
	public static void SetLock(ServerMain server, ServerPlayerData playerData, int groupUid, DateTime? expiresUtc)
	{
		Dictionary<int, long> locks = ReadLocks(playerData) ?? new Dictionary<int, long>();
		locks[groupUid] = expiresUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(expiresUtc.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : 0L;
		WriteLocks(server, playerData, locks);
	}

	public static bool ClearLock(ServerMain server, ServerPlayerData playerData, int groupUid)
	{
		Dictionary<int, long> locks = ReadLocks(playerData);
		if (locks == null || !locks.Remove(groupUid))
		{
			return false;
		}

		WriteLocks(server, playerData, locks);
		return true;
	}

	/// <summary>Live locks held by a player, expired ones dropped.</summary>
	public static List<KeyValuePair<int, DateTime?>> ListLocks(ServerMain server, ServerPlayerData playerData)
	{
		List<KeyValuePair<int, DateTime?>> result = new List<KeyValuePair<int, DateTime?>>();
		Dictionary<int, long> locks = ReadLocks(playerData);
		if (locks == null)
		{
			return result;
		}

		long now = NowMs();
		bool pruned = false;
		foreach (KeyValuePair<int, long> entry in locks.ToList())
		{
			if (entry.Value > 0 && entry.Value <= now)
			{
				locks.Remove(entry.Key);
				pruned = true;
				continue;
			}

			result.Add(new KeyValuePair<int, DateTime?>(entry.Key, entry.Value > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(entry.Value).UtcDateTime : null));
		}

		if (pruned)
		{
			WriteLocks(server, playerData, locks);
		}

		return result;
	}

	private static Dictionary<int, long> ReadLocks(ServerPlayerData playerData)
	{
		if (playerData?.CustomPlayerData == null || !playerData.CustomPlayerData.TryGetValue(LocksKey, out string json) || string.IsNullOrWhiteSpace(json))
		{
			return null;
		}

		try
		{
			return JsonConvert.DeserializeObject<Dictionary<int, long>>(json);
		}
		catch (Exception exception)
		{
			StratumRuntime.LogWarning("failed to read group locks for " + playerData.LastKnownPlayername + ": " + exception.Message);
			return null;
		}
	}

	private static void WriteLocks(ServerMain server, ServerPlayerData playerData, Dictionary<int, long> locks)
	{
		if (playerData == null)
		{
			return;
		}

		playerData.CustomPlayerData ??= new Dictionary<string, string>();
		if (locks == null || locks.Count == 0)
		{
			if (playerData.CustomPlayerData.Remove(LocksKey))
			{
				server.PlayerDataManager.playerDataDirty = true;
			}
			return;
		}

		string json = JsonConvert.SerializeObject(locks);
		if (!playerData.CustomPlayerData.TryGetValue(LocksKey, out string existing) || existing != json)
		{
			playerData.CustomPlayerData[LocksKey] = json;
			server.PlayerDataManager.playerDataDirty = true;
		}
	}

	// ---------------------------------------------------------------- relations

	public static string GetRelation(PlayerGroup from, int otherUid)
	{
		if (from?.StratumRelations == null || !from.StratumRelations.TryGetValue(otherUid, out string relation) || string.IsNullOrWhiteSpace(relation))
		{
			return RelationNeutral;
		}

		return relation;
	}

	/// <summary>
	/// Sets a relation on both groups at once. Staff decide relations here, so there is no
	/// request-and-accept step to keep the two sides consistent; writing both sides means a
	/// lookup never has to walk every group to find the mirror entry.
	/// </summary>
	public static void SetRelation(ServerMain server, PlayerGroup a, PlayerGroup b, string relation)
	{
		WriteRelation(a, b.Uid, relation);
		WriteRelation(b, a.Uid, relation);
		server.PlayerDataManager.playerGroupsDirty = true;
	}

	private static void WriteRelation(PlayerGroup group, int otherUid, string relation)
	{
		if (string.Equals(relation, RelationNeutral, StringComparison.OrdinalIgnoreCase))
		{
			group.StratumRelations?.Remove(otherUid);
			if (group.StratumRelations != null && group.StratumRelations.Count == 0)
			{
				group.StratumRelations = null;
			}
			return;
		}

		group.StratumRelations ??= new Dictionary<int, string>();
		group.StratumRelations[otherUid] = relation.ToLowerInvariant();
	}

	/// <summary>Relations of a group, skipping entries whose other group has been disbanded.</summary>
	public static List<KeyValuePair<PlayerGroup, string>> ListRelations(ServerMain server, PlayerGroup group)
	{
		List<KeyValuePair<PlayerGroup, string>> result = new List<KeyValuePair<PlayerGroup, string>>();
		if (group?.StratumRelations == null)
		{
			return result;
		}

		foreach (KeyValuePair<int, string> entry in group.StratumRelations)
		{
			if (server.PlayerDataManager.PlayerGroupsById.TryGetValue(entry.Key, out PlayerGroup other))
			{
				result.Add(new KeyValuePair<PlayerGroup, string>(other, entry.Value));
			}
		}

		return result;
	}

	// ---------------------------------------------------------------- friendly fire hooks

	private static bool GroupCountsAsSameSide(int groupUid)
	{
		if (!Enabled || installedServer == null)
		{
			return true;
		}

		if (!installedServer.PlayerDataManager.PlayerGroupsById.TryGetValue(groupUid, out PlayerGroup group))
		{
			return true;
		}

		return KindOf(group).CountsAsSameSide;
	}

	private static bool GroupsAreAllied(int groupUid, int otherGroupUid)
	{
		StratumGroupsConfig config = Config;
		if (!config.Enabled || !config.RelationsEnabled || !config.AlliesCountAsSameSide || installedServer == null)
		{
			return false;
		}

		if (!installedServer.PlayerDataManager.PlayerGroupsById.TryGetValue(groupUid, out PlayerGroup group))
		{
			return false;
		}

		// An explicit enemy relation on either side wins over an ally link, so staff can carve
		// one pairing out of a wider alliance.
		if (string.Equals(GetRelation(group, otherGroupUid), RelationEnemy, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		return string.Equals(GetRelation(group, otherGroupUid), RelationAlly, StringComparison.OrdinalIgnoreCase)
			&& GroupCountsAsSameSide(groupUid)
			&& GroupCountsAsSameSide(otherGroupUid);
	}

	// ---------------------------------------------------------------- tags

	/// <summary>
	/// The tag shown for a player, or null. Highest kind TagPriority wins, then the lowest group
	/// Uid, so the choice is stable rather than dependent on dictionary order.
	/// </summary>
	public static string ResolveTag(IServerPlayer player)
	{
		return installedServer == null ? null : ResolveTag(installedServer, player);
	}

	public static string ResolveTag(ServerMain server, IServerPlayer player)
	{
		StratumGroupsConfig config = Config;
		if (!config.Enabled || player is not ServerPlayer serverPlayer || serverPlayer.serverdata?.PlayerGroupMemberShips == null)
		{
			return null;
		}

		string bestTag = null;
		int bestPriority = int.MinValue;
		int bestUid = int.MaxValue;

		foreach (KeyValuePair<int, PlayerGroupMembership> membership in serverPlayer.serverdata.PlayerGroupMemberShips)
		{
			if (membership.Value == null || membership.Value.Level == EnumPlayerGroupMemberShip.None)
			{
				continue;
			}

			if (!server.PlayerDataManager.PlayerGroupsById.TryGetValue(membership.Key, out PlayerGroup group))
			{
				continue;
			}

			StratumGroupKindConfig kind = KindOf(group);
			string tag = string.IsNullOrWhiteSpace(group.StratumTag) ? kind.Tag : group.StratumTag;
			if (string.IsNullOrWhiteSpace(tag))
			{
				continue;
			}

			if (kind.TagPriority > bestPriority || (kind.TagPriority == bestPriority && group.Uid < bestUid))
			{
				bestTag = tag;
				bestPriority = kind.TagPriority;
				bestUid = group.Uid;
			}
		}

		return bestTag;
	}

	public static string FormatTag(string tag)
	{
		if (string.IsNullOrWhiteSpace(tag))
		{
			return null;
		}

		return Config.TagFormat.Replace("{tag}", tag);
	}

	// ---------------------------------------------------------------- shared helpers

	public static void LogAudit(string message)
	{
		StratumRuntime.LogAudit(message, true);
	}

	public static string FormatUntil(DateTime? expiresUtc)
	{
		if (!expiresUtc.HasValue)
		{
			return string.Empty;
		}

		return " until " + expiresUtc.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
	}

	private static long NowMs()
	{
		return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
	}
}
