using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AsAboveSoBelow;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace MultiDeckGravships
{
	public static class HarmonyInit
	{
		public const string Id = "divisici.multideckgravships";

		public static int Applied { get; private set; }

		public static int Failed { get; private set; }

		public static readonly List<MethodInfo> Replacements = new List<MethodInfo>();

		private static Harmony harmony;

		public static void Apply()
		{
			if (harmony != null)
			{
				return;
			}
			harmony = new Harmony(Id);
			if (!Deck.Init(out string error))
			{
				MDGLog.Error("disabled: " + error + ". Gravships fly as they would without this mod.");
				return;
			}
			Type engine = typeof(Building_GravEngine);
			Type gu = typeof(GravshipUtility);
			Type gpu = typeof(GravshipPlacementUtility);

			// Pawns, facilities and boarding see the upper decks as part of the ship.
			Patch(AccessTools.Method(engine, "ValidSubstructureAt"), post: nameof(EnginePatches.ValidSubstructureAt_Postfix));
			Patch(AccessTools.Method(engine, "OnValidSubstructure"), post: nameof(EnginePatches.OnValidSubstructure_Postfix));
			Patch(AccessTools.Method(gu, "IsOnboardGravship_NewTemp"), post: nameof(EnginePatches.IsOnboardGravship_Postfix));

			// Launch: capture the upper decks with the ship.
			Patch(AccessTools.Constructor(typeof(Gravship), new[] { engine }), post: nameof(LaunchPatches.Ctor_Postfix), trans: nameof(LaunchPatches.FirstValidSubstructure_Transpiler));
			Patch(AccessTools.Method(gu, "GenerateGravship"), pre: nameof(LaunchPatches.Generate_Prefix), post: nameof(LaunchPatches.Generate_Postfix), trans: nameof(LaunchPatches.AllValidSubstructure_Transpiler), fin: nameof(LaunchPatches.Generate_Finalizer));

			// Landing: put each upper-deck offset on its own level.
			Patch(AccessTools.Method(gpu, "PlaceGravshipInMap"), pre: nameof(PlacePatches.Place_Prefix), post: nameof(PlacePatches.Place_Postfix), fin: nameof(PlacePatches.Place_Finalizer));
			Patch(AccessTools.Method(gpu, "ApplyTemperatureVacuumFromBase"), pre: nameof(PlacePatches.ApplyTemperature_Prefix), trans: nameof(PlacePatches.Add_Transpiler));
			Patch(AccessTools.Method(typeof(WorldComponent_GravshipController), "PlaceGravship"), fin: nameof(PlacePatches.ControllerPlace_Finalizer));
			MethodBase[] addSites =
			{
				AccessTools.Method(gpu, "SpawnTerrain", new[] { typeof(Gravship), typeof(Map), typeof(IntVec3) }),
				AccessTools.Method(gpu, "SpawnTerrain", new[] { typeof(Map), typeof(IntVec3), typeof(Dictionary<IntVec3, TerrainDef>), typeof(bool) }),
				AccessTools.Method(gpu, "SpawnPawns"),
				AccessTools.Method(gpu, "SpawnNonPawnThings"),
				AccessTools.Method(gpu, "SpawnRoofs"),
				AccessTools.Method(gpu, "SpawnGas"),
				AccessTools.Method(gpu, "CopyDesignations"),
				AccessTools.Method(gpu, "CopyAreasIntoMap"),
				AccessTools.Method(typeof(MoveableArea_Allowed), "TryCreateArea"),
				AccessTools.Method(typeof(MoveableStockpile), "TryCreateStockpile"),
				AccessTools.Method(typeof(MoveableGrowZone), "TryCreateGrowZone")
			};
			foreach (MethodBase m in addSites)
			{
				Patch(m, trans: nameof(PlacePatches.Add_Transpiler));
			}
			// Vanilla Gravship Expanded recolours vac-barrier roofs after landing with the same offsets.
			MethodInfo vgeRoofs = AccessTools.Method("VanillaGravshipExpanded.GravshipPlacementUtility_SpawnRoofs:Postfix");
			if (vgeRoofs != null)
			{
				Patch(vgeRoofs, trans: nameof(PlacePatches.Add_Transpiler));
			}

			// The landing spot must have room for the upper decks too.
			Patch(AccessTools.Method(typeof(Designator_MoveGravship), "ValidGravshipLocation"), post: nameof(DesignatorPatches.ValidGravshipLocation_Postfix), postPriority: Priority.Last);

			// AASB2 stairs on the ship travel as a linked pair instead of collapsing and rebuilding.
			Patch(AccessTools.Method(typeof(Building_ABStairs2), "SpawnSetup"), pre: nameof(StairsPatches.SpawnSetup_Prefix), fin: nameof(StairsPatches.SpawnSetup_Finalizer));
			Patch(AccessTools.Method(typeof(Building_ABStairs2), "DeSpawn"), pre: nameof(StairsPatches.DeSpawn_Prefix), fin: nameof(StairsPatches.DeSpawn_Finalizer));

			// Ship parts that need substructure (hull, thrusters, tanks...) can be built on the upper decks.
			Patch(AccessTools.Method(typeof(GridsUtility), "GetAffordances", new[] { typeof(IntVec3), typeof(Map) }), post: nameof(AffordancePatches.GetAffordances_Postfix));

			// Orbit: ship roofs hold a deck above them.
			Type orbit = AccessTools.TypeByName("AsAboveSoBelow.ABOrbitRules");
			Patch(AccessTools.Method(orbit, "ProjectsUpperLevel"), post: nameof(OrbitPatches.ProjectsUpperLevel_Postfix));
			Patch(AccessTools.Method(orbit, "LinkMayRise"), post: nameof(OrbitPatches.LinkMayRise_Postfix));

			string summary = Applied + " patches applied";
			if (Failed > 0)
			{
				MDGLog.Error(summary + ", " + Failed + " FAILED (see above). Upper decks may not travel correctly.");
			}
			else
			{
				MDGLog.Message(summary + ".");
			}
		}

		private static void Patch(MethodBase target, string pre = null, string post = null, string trans = null, string fin = null, int postPriority = -1)
		{
			if (target == null)
			{
				Failed++;
				MDGLog.Error("patch target not found for " + (pre ?? post ?? trans ?? fin) + " (game or As above, So below II changed).");
				return;
			}
			try
			{
				MethodInfo replacement = harmony.Patch(target, Hm(pre), Hm(post, postPriority), Hm(trans), Hm(fin));
				if (replacement != null)
				{
					Replacements.Add(replacement);
				}
				Applied++;
			}
			catch (Exception e)
			{
				Failed++;
				MDGLog.Error("could not patch " + target.FullDescription() + ": " + e);
			}
		}

		private static HarmonyMethod Hm(string name, int priority = -1)
		{
			if (name == null)
			{
				return null;
			}
			foreach (Type t in new[] { typeof(EnginePatches), typeof(LaunchPatches), typeof(PlacePatches), typeof(DesignatorPatches), typeof(StairsPatches), typeof(OrbitPatches), typeof(AffordancePatches) })
			{
				MethodInfo m = AccessTools.Method(t, name);
				if (m != null)
				{
					HarmonyMethod hm = new HarmonyMethod(m);
					if (priority >= 0)
					{
						hm.priority = priority;
					}
					return hm;
				}
			}
			throw new MissingMethodException("patch method " + name);
		}
	}

	public static class EnginePatches
	{
		public static void ValidSubstructureAt_Postfix(Building_GravEngine __instance, IntVec3 cell, ref bool __result)
		{
			if (!__result && Deck.IsUpperCell(__instance, cell))
			{
				__result = true;
			}
		}

		public static void OnValidSubstructure_Postfix(Building_GravEngine __instance, Thing thing, ref bool __result)
		{
			if (__result || thing == null || !thing.Spawned || !Deck.Ready)
			{
				return;
			}
			__result = Deck.ThingOnShip(__instance, thing);
		}

		public static void IsOnboardGravship_Postfix(IntVec3 cell, Building_GravEngine engine, Pawn pawn, bool desperate, bool respectAllowedAreas, ref bool __result)
		{
			if (__result || engine == null || !engine.Spawned || !Deck.IsUpperCell(engine, cell))
			{
				return;
			}
			if (respectAllowedAreas && pawn != null && !cell.InAllowedArea(pawn))
			{
				return;
			}
			if (!desperate)
			{
				Room room = cell.GetRoom(engine.Map);
				if (room != null && room.PsychologicallyOutdoors)
				{
					return;
				}
			}
			__result = true;
		}
	}

	public static class LaunchPatches
	{
		private static readonly MethodInfo GetValidSubstructure = AccessTools.PropertyGetter(typeof(Building_GravEngine), "ValidSubstructure");

		private static readonly MethodInfo CaptureSet = AccessTools.Method(typeof(Deck), nameof(Deck.CaptureSet));

		/// <summary>Gravship(engine): the first ValidSubstructure read is the set of cells it copies.</summary>
		public static IEnumerable<CodeInstruction> FirstValidSubstructure_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
		{
			return Swap(instructions, __originalMethod, firstOnly: true);
		}

		/// <summary>GenerateGravship: the cells it clears off the old map.</summary>
		public static IEnumerable<CodeInstruction> AllValidSubstructure_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
		{
			return Swap(instructions, __originalMethod, firstOnly: false);
		}

		private static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, MethodBase original, bool firstOnly)
		{
			int n = 0;
			foreach (CodeInstruction ci in instructions)
			{
				if ((!firstOnly || n == 0) && ci.Calls(GetValidSubstructure))
				{
					ci.opcode = OpCodes.Call;
					ci.operand = CaptureSet;
					n++;
				}
				yield return ci;
			}
			if (n == 0)
			{
				MDGLog.Error("found no ValidSubstructure read in " + original.FullDescription() + "; upper decks will not launch.");
			}
		}

		public static void Ctor_Postfix(Gravship __instance, Building_GravEngine engine)
		{
			try
			{
				Deck.ConvertCapturedOffsets(__instance, engine);
			}
			catch (Exception e)
			{
				MDGLog.Error("re-keying the upper deck failed: " + e);
			}
		}

		public static void Generate_Prefix(Building_GravEngine engine)
		{
			try
			{
				Deck.BeginLaunch(engine);
			}
			catch (Exception e)
			{
				Deck.CleanupLaunch();
				MDGLog.Error("measuring the upper deck failed, launching the main deck only: " + e);
			}
		}

		public static void Generate_Postfix(Gravship __result)
		{
			Deck.FinishLaunch(__result);
		}

		public static Exception Generate_Finalizer(Exception __exception)
		{
			Deck.CleanupLaunch();
			return __exception;
		}
	}

	public static class PlacePatches
	{
		private static readonly MethodInfo OpAddition = AccessTools.Method(typeof(IntVec3), "op_Addition", new[] { typeof(IntVec3), typeof(IntVec3) });

		private static readonly MethodInfo DeckAdd = AccessTools.Method(typeof(Deck), nameof(Deck.Add));

		public static IEnumerable<CodeInstruction> Add_Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
		{
			int n = 0;
			foreach (CodeInstruction ci in instructions)
			{
				if (ci.Calls(OpAddition))
				{
					ci.operand = DeckAdd;
					n++;
				}
				yield return ci;
			}
			if (n == 0)
			{
				MDGLog.Warning("found no cell offsets in " + __originalMethod.FullDescription() + "; its upper-deck parts will land on the main deck.");
			}
		}

		public static void Place_Prefix(Gravship gravship, IntVec3 root, Map map)
		{
			try
			{
				Deck.BeginPlace(gravship, root, map);
			}
			catch (Exception e)
			{
				MDGLog.Error("preparing the upper deck for landing failed: " + e);
			}
		}

		public static void Place_Postfix(Gravship gravship)
		{
			Deck.FinishPlace(gravship);
		}

		public static Exception Place_Finalizer(Exception __exception)
		{
			Deck.PlaceFinalizer();
			return __exception;
		}

		/// <summary>Called right after PlaceGravshipInMap by the controller; also by others, so set up a context if needed.</summary>
		public static void ApplyTemperature_Prefix(Gravship gravship, IntVec3 root, Map map)
		{
			if (Deck.place == null && gravship != null && map != null && Deck.Ready)
			{
				AsAboveSoBelow.ABBandMap bands = Deck.Bands(map);
				Deck.place = new Deck.PlaceCtx
				{
					gravship = gravship,
					map = map,
					bands = bands,
					root = root,
					shipBand = (bands != null) ? bands.BandOf(root) : 0
				};
			}
		}

		public static Exception ControllerPlace_Finalizer(Exception __exception)
		{
			Deck.EndPlace();
			return __exception;
		}
	}

	public static class DesignatorPatches
	{
		private static readonly AccessTools.FieldRef<Designator_MoveGravship, GravshipLandingMarker> Marker = AccessTools.FieldRefAccess<Designator_MoveGravship, GravshipLandingMarker>("marker");

		private static readonly MethodInfo GetSizeRotAdjustedCell = AccessTools.Method(typeof(Designator_MoveGravship), "GetSizeRotAdjustedCell");

		public static void ValidGravshipLocation_Postfix(Designator_MoveGravship __instance, IntVec3 loc, List<IntVec3> validCells, List<IntVec3> invalidCells, ref AcceptanceReport __result)
		{
			if (!__result.Accepted || !Deck.Ready || GetSizeRotAdjustedCell == null)
			{
				return;
			}
			try
			{
				GravshipLandingMarker marker = Marker(__instance);
				Gravship g = marker?.gravship;
				Map map = Find.CurrentMap;
				if (g == null || map == null || Deck.Bands(map) == null)
				{
					return;
				}
				IntVec3 adjusted = (IntVec3)GetSizeRotAdjustedCell.Invoke(__instance, new object[] { loc });
				IntVec3 root = PrefabUtility.GetRoot(adjusted, g.Bounds.Size, marker.GravshipRotation);
				AcceptanceReport upper = Deck.CheckUpperLanding(g, root, map, validCells, invalidCells);
				if (!upper.Accepted)
				{
					__result = upper;
				}
			}
			catch (Exception e)
			{
				Log.ErrorOnce(MDGLog.Prefix + "upper deck landing check failed: " + e, 0x4D44_4701);
			}
		}
	}

	public static class StairsPatches
	{
		/// <summary>While a ship is being set down, its stairs must not build fresh counterparts: their partners are on board.</summary>
		public static void SpawnSetup_Prefix(bool respawningAfterLoad, out bool __state)
		{
			__state = false;
			if (respawningAfterLoad || Deck.place == null || !GravshipPlacementUtility.placingGravship)
			{
				return;
			}
			__state = Deck.SetStaticBool(Deck.F_spawningCounterpart, true);
		}

		public static Exception SpawnSetup_Finalizer(Exception __exception, bool __state)
		{
			if (__state)
			{
				Deck.SetStaticBool(Deck.F_spawningCounterpart, false);
			}
			return __exception;
		}

		/// <summary>While a ship is lifting off, removing one end of a staircase must not destroy the other end, which is coming along.</summary>
		public static void DeSpawn_Prefix(out bool __state)
		{
			__state = false;
			if (Deck.launch == null || !GravshipUtility.generatingGravship)
			{
				return;
			}
			__state = Deck.SetStaticBool(Deck.F_collapsingShaft, true);
		}

		public static Exception DeSpawn_Finalizer(Exception __exception, bool __state)
		{
			if (__state)
			{
				Deck.SetStaticBool(Deck.F_collapsingShaft, false);
			}
			return __exception;
		}
	}

	/// <summary>
	/// Vanilla only lets ship parts stand on cells whose foundation is substructure. An upper
	/// deck has no foundation, just AASB2's rooftop (or a floor laid on it), so on cells the
	/// ship holds up the affordance list gains Substructure.
	/// </summary>
	public static class AffordancePatches
	{
		private static TerrainAffordanceDef substructure;

		private static readonly Dictionary<List<TerrainAffordanceDef>, List<TerrainAffordanceDef>> withSubstructure = new Dictionary<List<TerrainAffordanceDef>, List<TerrainAffordanceDef>>();

		public static void GetAffordances_Postfix(IntVec3 c, Map map, ref List<TerrainAffordanceDef> __result)
		{
			if (__result == null || map == null || !Deck.Ready)
			{
				return;
			}
			if (substructure == null)
			{
				substructure = DefDatabase<TerrainAffordanceDef>.GetNamedSilentFail("Substructure");
				if (substructure == null)
				{
					return;
				}
			}
			if (!Deck.IsShipDeckCell(map, c) || __result.Contains(substructure))
			{
				return;
			}
			if (!withSubstructure.TryGetValue(__result, out List<TerrainAffordanceDef> list))
			{
				list = new List<TerrainAffordanceDef>(__result) { substructure };
				withSubstructure[__result] = list;
			}
			__result = list;
		}
	}

	public static class OrbitPatches
	{
		public static void ProjectsUpperLevel_Postfix(Map map, IntVec3 below, ref bool __result)
		{
			if (__result || MDGMod.Settings == null || !MDGMod.Settings.orbitDecks || map == null || !below.InBounds(map))
			{
				return;
			}
			if (Deck.IsShipStructure(map, below))
			{
				__result = true;
			}
		}

		public static void LinkMayRise_Postfix(Map map, CellRect footprint, ref bool __result)
		{
			if (__result || MDGMod.Settings == null || !MDGMod.Settings.orbitDecks || map == null)
			{
				return;
			}
			foreach (IntVec3 c in footprint)
			{
				if (!c.InBounds(map) || !Deck.IsShipStructure(map, c))
				{
					return;
				}
			}
			__result = true;
		}
	}

	/// <summary>Gives orbital ships their rooftops back on maps saved while AASB2 suppressed them.</summary>
	public class MapComponent_OrbitDeckResync : MapComponent
	{
		public MapComponent_OrbitDeckResync(Map map) : base(map)
		{
		}

		public override void FinalizeInit()
		{
			try
			{
				if (!Deck.Ready || MDGMod.Settings == null || !MDGMod.Settings.orbitDecks || !ModsConfig.OdysseyActive)
				{
					return;
				}
				if (map.Biome == null || !map.Biome.inVacuum || Deck.Bands(map) == null)
				{
					return;
				}
				int n = 0;
				foreach (IntVec3 c in map.AllCells)
				{
					if (map.roofGrid.RoofAt(c) != null && Deck.IsShipStructure(map, c))
					{
						ABSkySync.SyncAbove(map, c);
						n++;
					}
				}
				MDGLog.Dev("orbit rooftop resync on map " + map.uniqueID + ": " + n + " roofed ship cell(s) checked.");
			}
			catch (Exception e)
			{
				MDGLog.Warning("orbit rooftop resync failed: " + e.Message);
			}
		}
	}
}
