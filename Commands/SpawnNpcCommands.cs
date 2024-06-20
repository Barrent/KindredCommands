using System.Linq;
using KindredCommands.Data;
using KindredCommands.Models;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.Gameplay.Scripting;
using ProjectM.Scripting;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using VampireCommandFramework;

namespace KindredCommands.Commands;

internal static class SpawnCommands
{
	public record struct CharacterUnit(string Name, PrefabGUID Prefab);

	public class CharacterUnitConverter : CommandArgumentConverter<CharacterUnit>
	{
		public override CharacterUnit Parse(ICommandContext ctx, string input)
		{
			if (Character.Named.TryGetValue(input, out var unit) || Character.Named.TryGetValue("CHAR_" + input, out unit))
			{
				return new(Character.NameFromPrefab[unit.GuidHash], unit);
			}
			// "CHAR_Bandit_Bomber": -1128238456,
			if (int.TryParse(input, out var id) && Character.NameFromPrefab.TryGetValue(id, out var name))
			{
				return new(name, new(id));
			}

			throw ctx.Error($"Can't find unit {input.Bold()}");
		}
	}

	[Command("spawnnpc", "spwn", description: "Spawns CHAR_ npcs", adminOnly: true)]
	public static void SpawnNpc(ChatCommandContext ctx, CharacterUnit character, int count = 1)
	{
		if (Database.IsSpawnBanned(character.Name, out var reason))
		{
			throw ctx.Error($"Cannot spawn {character.Name.Bold()} because it is banned. Reason: {reason}");
		}

		var pos = Core.EntityManager.GetComponentData<LocalToWorld>(ctx.Event.SenderCharacterEntity).Position;

		Services.UnitSpawnerService.Spawn(ctx.Event.SenderUserEntity, character.Prefab, count, new(pos.x, pos.z), 1, 2, -1);
		ctx.Reply($"Spawning {count} {character.Name.Bold()} at your position");
	}

	[Command("customspawn", "cspwn", "customspawn <Prefab Name> [<BloodType> <BloodQuality> <Consumable(\"true/false\")> <Duration> <level>]", "Spawns a modified NPC at your current position.", adminOnly: true)]
	public static void CustomSpawnNpc(ChatCommandContext ctx, CharacterUnit unit, BloodType type = BloodType.Frailed, int quality = 0, bool consumable = true, int duration = -1, int level = 0)
	{
		if (Database.IsSpawnBanned(unit.Name, out var reason))
		{
			throw ctx.Error($"Cannot spawn {unit.Name.Bold()} because it is banned. Reason: {reason}");
		}

		var pos = Core.EntityManager.GetComponentData<LocalToWorld>(ctx.Event.SenderCharacterEntity).Position;

		if (quality > 100 || quality < 0)
		{
			throw ctx.Error($"Blood Quality must be between 0 and 100");
		}

		Core.UnitSpawner.SpawnWithCallback(ctx.Event.SenderUserEntity, unit.Prefab, pos.xz, duration, (Entity e) =>
		{
			var blood = Core.EntityManager.GetComponentData<BloodConsumeSource>(e);
			blood.UnitBloodType._Value = new PrefabGUID((int)type);
			blood.BloodQuality = quality;
			blood.CanBeConsumed = consumable;
			Core.EntityManager.SetComponentData(e, blood);

			if (level > 0)
			{
				Buffs.AddBuff(ctx.Event.SenderUserEntity, e, Prefabs.BoostedBuff1, -1, true);
				if (BuffUtility.TryGetBuff(Core.EntityManager, e, Prefabs.BoostedBuff1, out var buffEntity))
				{
					buffEntity.Remove<SpawnStructure_WeakenState_DataShared>();
					buffEntity.Remove<ScriptSpawn>();
					buffEntity.Add<ModifyUnitLevelBuff>();
					buffEntity.Write(new ModifyUnitLevelBuff()
					{
						UnitLevel = level
					});
				}
			}
		});
		ctx.Reply($"Spawning {unit.Name.Bold()} with {quality}% {type} blood at your position. It is Lvl{level} and will live {(duration<0?"until killed":$"{duration} seconds")}.");
	}


	[Command("despawnnpc", "dspwn", description: "Despawns CHAR_ npcs", adminOnly: true)]
	public static void DespawnNpc(ChatCommandContext ctx, CharacterUnit character, float radius = 25f)
	{
		var charEntity = ctx.Event.SenderCharacterEntity;
		var pos = charEntity.Read<Translation>().Value.xz;
		var entities = Helper.GetAllEntitiesInRadius<PrefabGUID>(pos, radius).Where(e => e.Read<PrefabGUID>().Equals(character.Prefab));
		var count = 0;
		foreach (var e in entities)
		{
			StatChangeUtility.KillOrDestroyEntity(Core.EntityManager, e, charEntity, charEntity, Time.time, StatChangeReason.Default, true);
			count++;
		}
		ctx.Reply($"You've killed {count} {character.Name.Bold()} at your position. You murderer!");
	}

	[Command("spawnhorse", "sh", description: "Spawns a horse", adminOnly: true)] 
	public static void SpawnHorse(ChatCommandContext ctx, float speed, float acceleration, float rotation, bool spectral=false, int num=1)
	{
		var pos = Core.EntityManager.GetComponentData<LocalToWorld>(ctx.Event.SenderCharacterEntity).Position;
		var horsePrefab = spectral ? Prefabs.CHAR_Mount_Horse_Spectral : Prefabs.CHAR_Mount_Horse;

		for (int i = 0; i < num; i++)
		{
			Core.UnitSpawner.SpawnWithCallback(ctx.Event.SenderUserEntity, horsePrefab, pos.xz, -1, (Entity horse) =>
			{
				var mount = horse.Read<Mountable>();
				mount.MaxSpeed = speed;
				mount.Acceleration = acceleration;
				mount.RotationSpeed = rotation * 10f;
				horse.Write<Mountable>(mount);
			});
		}

		ctx.Reply($"Spawned {num}{(spectral == false ? "" : " spectral")} horse{(num > 1 ? "s" : "")} (with speed:{speed}, accel:{acceleration}, and rotate:{rotation}) near you.");
	}


	[Command("spawnban", description: "Shows which GUIDs are banned and why.", adminOnly: true)]
	public static void SpawnBan(ChatCommandContext ctx, CharacterUnit character, string reason)
	{
		Database.SetNoSpawn(character.Name, reason);
		ctx.Reply($"Banned '{character.Name}' from spawning with reason '{reason}'");
	}

	readonly static float3 banishLocation = new(-1551.8973f, 5, -2728.9856f);

    [Command("banishhorse", "bh", description: "Banishes dominated ghost horses on the server out of bounds", adminOnly: true)]
	public static void BanishGhost(ChatCommandContext ctx)
	{
		var horses = Helper.GetEntitiesByComponentTypes<Immortal, Mountable>(true).ToArray()
                        .Where(x => x.Read<PrefabGUID>().GuidHash == Prefabs.CHAR_Mount_Horse_Vampire.GuidHash)
                        .Where(x => BuffUtility.HasBuff(Core.EntityManager, x, Prefabs.Buff_General_VampireMount_Dead));

		var horsesToBanish = horses.Where(x => Vector3.Distance(banishLocation, x.Read<LocalToWorld>().Position) > 30f);

        if (horsesToBanish.Any())
		{
			foreach (var horse in horsesToBanish)
			{
				Core.EntityManager.SetComponentData(horse, new LastTranslation { Value = banishLocation });
				Core.EntityManager.SetComponentData(horse, new Translation { Value = banishLocation });
			}
			ctx.Reply($"Banished {horsesToBanish.Count()} ghost horse{(horsesToBanish.Count() > 1 ? "s" : "")}");
		}
		else
		{
			ctx.Reply($"No valid ghost horses found to banish but {horses.Count()} already banished");
		}
    }
	[Command("teleporthorse", description: "teleports horses to you", adminOnly: true)]
	public static void TeleportHorse(ChatCommandContext ctx, float radius = 5f)
	{
		var charEntity = ctx.Event.SenderCharacterEntity;
		var pos = charEntity.Read<Translation>().Value.xz;
		var entities = Helper.GetAllEntitiesInRadius<Mountable>(pos, radius).Where(e => e.Read<PrefabGUID>().Equals(Prefabs.CHAR_Mount_Horse_Vampire));
		var count = 0;
		foreach (var e in entities)
		{
			Core.EntityManager.SetComponentData(e, new Translation { Value = Core.EntityManager.GetComponentData<LocalToWorld>(ctx.Event.SenderCharacterEntity).Position });
			count++;
		}

		ctx.Reply($"You've teleported {count} horses to your position.");
	}

	/*
	[Command("deletehorses", description: "Deletes all horses including player horses", adminOnly: true)]
	public static void DeleteHorses(ChatCommandContext ctx)
	{
		var horses = Helper.GetEntitiesByComponentType<Mountable>(true).ToArray()
			.Where(x => x.Read<PrefabGUID>().GuidHash == Prefabs.CHAR_Mount_Horse.GuidHash || x.Read<PrefabGUID>().GuidHash == Prefabs.CHAR_Mount_Horse_Vampire.GuidHash);
		foreach (var horse in horses)
		{
			StatChangeUtility.KillEntity(Core.EntityManager, horse, ctx.Event.SenderCharacterEntity, Time.time, true);
		}
		ctx.Reply($"You've killed {horses.Count()} horses.");
	}*/
}
