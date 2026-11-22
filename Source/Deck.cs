using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using AsAboveSoBelow;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace MultiDeckGravships
{
	/// <summary>
	/// The upper decks of a gravship on an As above, So below II colony.
	///
	/// AASB2 stacks its levels as horizontal bands of one tall map, so the deck above a ship
	/// is a different band of the same map: cell (x, z) on the ship is (x, z + k * Slot) on
	/// level +k. Vanilla stores every captured cell as an offset from the grav engine and
	/// rotates that offset on landing. An upper-deck offset is stored with its LEVEL in the
	/// otherwise unused IntVec3.y and its in-band offset in x/z, so the rotation turns the
	/// deck with the ship and the save keeps the level. Every place that turns an offset back
	/// into a map cell (root + local) is transpiled to Deck.Add, which lifts y != 0 offsets
	/// onto the right band of the landing map.
	/// </summary>
	public static class Deck
	{
		public static bool Ready { get; private set; }

		// ---------------------------------------------------------------- reflection

		internal static AccessTools.FieldRef<Gravship, Dictionary<Thing, PositionData>> G_things;
		internal static AccessTools.FieldRef<Gravship, Dictionary<Pawn, PositionData>> G_pawns;
		internal static AccessTools.FieldRef<Gravship, Dictionary<Thing, PositionData>> G_thrusters;
		internal static AccessTools.FieldRef<Gravship, Dictionary<Thing, PositionData>> G_exteriorDoors;
		internal static AccessTools.FieldRef<Gravship, Dictionary<Thing, bool>> G_powerOn;
		internal static AccessTools.FieldRef<Gravship, Dictionary<Thing, Thing>> G_connectParents;
		internal static AccessTools.FieldRef<Gravship, Dictionary<IntVec3, RoofDef>> G_roofs;
		internal static AccessTools.FieldRef<Gravship, Dictionary<IntVec3, TerrainDef>> G_foundations;
		internal static AccessTools.FieldRef<Gravship, Dictionary<IntVec3, TerrainDef>> G_terrains;
		internal static AccessTools.FieldRef<Gravship, Dictionary<IntVec3, ColorDef>> G_terrainColors;
		internal static AccessTools.FieldRef<Gravship, Dictionary<IntVec3, Designation>> G_terrainDesignations;
		internal static AccessTools.FieldRef<Gravship, Dictionary<IntVec3, uint>> G_gases;
		internal static AccessTools.FieldRef<Gravship, List<CellRect>> G_occupiedRects;
		internal static AccessTools.FieldRef<Gravship, List<RoomTemperatureVacuum>> G_roomTemperatures;
		internal static AccessTools.FieldRef<Gravship, List<IntVec3>> G_gravFieldExtenderPositions;
		internal static AccessTools.FieldRef<Gravship, CellRect> G_bounds;
		internal static AccessTools.FieldRef<MoveableArea, List<IntVec3>> A_relativeCells;
		internal static MethodInfo M_UpdateBoundingBoxes;
		internal static FieldInfo[] F_placementCaches;

		// AASB2 internals
		internal static FieldInfo F_counterparts;
		internal static FieldInfo F_spawningCounterpart;
		internal static FieldInfo F_collapsingShaft;
		internal static MethodInfo M_TryEstablish;
		internal static FieldInfo F_syncSuspended;
		internal static FieldInfo F_decorWindowMap;

		// Vanilla Gravship Expanded (optional)
		internal static FieldInfo F_vgeTempVacColors;
		internal static FieldInfo F_vgeCurrentGravship;

		internal static bool Init(out string error)
		{
			error = null;
			try
			{
				G_things = AccessTools.FieldRefAccess<Gravship, Dictionary<Thing, PositionData>>("things");
				G_pawns = AccessTools.FieldRefAccess<Gravship, Dictionary<Pawn, PositionData>>("pawns");
				G_thrusters = AccessTools.FieldRefAccess<Gravship, Dictionary<Thing, PositionData>>("thrusters");
				G_exteriorDoors = AccessTools.FieldRefAccess<Gravship, Dictionary<Thing, PositionData>>("exteriorDoors");
				G_powerOn = AccessTools.FieldRefAccess<Gravship, Dictionary<Thing, bool>>("powerOn");
				G_connectParents = AccessTools.FieldRefAccess<Gravship, Dictionary<Thing, Thing>>("connectParents");
				G_roofs = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, RoofDef>>("roofs");
				G_foundations = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, TerrainDef>>("foundations");
				G_terrains = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, TerrainDef>>("terrains");
				G_terrainColors = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, ColorDef>>("terrainColors");
				G_terrainDesignations = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, Designation>>("terrainDesignations");
				G_gases = AccessTools.FieldRefAccess<Gravship, Dictionary<IntVec3, uint>>("gases");
				G_occupiedRects = AccessTools.FieldRefAccess<Gravship, List<CellRect>>("occupiedRects");
				G_roomTemperatures = AccessTools.FieldRefAccess<Gravship, List<RoomTemperatureVacuum>>("roomTemperatures");
				G_gravFieldExtenderPositions = AccessTools.FieldRefAccess<Gravship, List<IntVec3>>("gravFieldExtenderPositions");
				G_bounds = AccessTools.FieldRefAccess<Gravship, CellRect>("bounds");
				A_relativeCells = AccessTools.FieldRefAccess<MoveableArea, List<IntVec3>>("relativeCells");
				M_UpdateBoundingBoxes = Need(AccessTools.Method(typeof(Gravship), "UpdateBoundingBoxes"), "Gravship.UpdateBoundingBoxes");
				string[] caches = { "tmpThingPlacements", "tmpPawns", "tmpThrusterPlacements", "tmpExteriorDoorPlacements", "tmpFoundations", "tmpTerrains" };
				F_placementCaches = caches.Select(n => Need(AccessTools.Field(typeof(Gravship), n), "Gravship." + n)).ToArray();

				F_counterparts = Need(AccessTools.Field(typeof(Building_ABStairs2), "counterparts"), "Building_ABStairs2.counterparts");
				F_spawningCounterpart = Need(AccessTools.Field(typeof(Building_ABStairs2), "spawningCounterpart"), "Building_ABStairs2.spawningCounterpart");
				F_collapsingShaft = Need(AccessTools.Field(typeof(Building_ABStairs2), "collapsingShaft"), "Building_ABStairs2.collapsingShaft");
				M_TryEstablish = Need(AccessTools.Method(typeof(Building_ABStairs2), "TryEstablish"), "Building_ABStairs2.TryEstablish");
				F_syncSuspended = Need(AccessTools.Field(typeof(ABSkySync), "Suspended"), "ABSkySync.Suspended");
				// Optional: only used to keep AASB2's map-generation decoration nudge off a landing deck.
				F_decorWindowMap = AccessTools.Field(typeof(ABAirSpawnGuard), "decorWindowMap");

				Type vge = AccessTools.TypeByName("VanillaGravshipExpanded.Gravship_CopyCellContents");
				if (vge != null)
				{
					F_vgeTempVacColors = AccessTools.Field(vge, "tempVacBarrierRoofColors");
					F_vgeCurrentGravship = AccessTools.Field(vge, "currentGravship");
				}
			}
			catch (Exception e)
			{
				error = e.Message;
				Ready = false;
				return false;
			}
			Ready = true;
			return true;
		}

		private static T Need<T>(T member, string what) where T : class
		{
			if (member == null)
			{
				throw new MissingMemberException(what + " not found (game or As above, So below II changed)");
			}
			return member;
		}

		// ---------------------------------------------------------------- band helpers

		public static ABBandMap Bands(Map map)
		{
			if (map == null)
			{
				return null;
			}
			ABBandMap b = ABBands.CompOf(map);
			return (b != null && b.Banded) ? b : null;
		}

		/// <summary>The ground layer of a cell: the terrain under a laid floor, or the terrain itself.</summary>
		public static TerrainDef BaseLayer(Map map, IntVec3 c)
		{
			return map.terrainGrid.UnderTerrainAt(c) ?? map.terrainGrid.TerrainAt(c);
		}

		/// <summary>Ground that only exists because the ship holds it up: a rooftop or a wall top.</summary>
		public static bool IsDeckBase(TerrainDef t)
		{
			return t != null && (t == ABDefOf.AB_RoofSurface || t == ABDefOf.AB_WallTop);
		}

		/// <summary>Terrain AASB2 derives from the level below (its own, free to overwrite).</summary>
		public static bool IsDerived(TerrainDef t)
		{
			return t != null && (t == ABDefOf.AB_OpenAir || IsDeckBase(t));
		}

		/// <summary>
		/// A rooftop or wall top that a gravship holds up: walking straight down through the
		/// levels below, every cell is ship deck until one stands on substructure.
		/// </summary>
		public static bool IsShipDeckCell(Map map, IntVec3 c)
		{
			if (!c.InBounds(map) || !IsDeckBase(BaseLayer(map, c)))
			{
				return false;
			}
			ABBandMap bands = Bands(map);
			if (bands == null)
			{
				return false;
			}
			IntVec3 cur = c;
			for (int band = bands.BandOf(c) - 1; band >= 0; band--)
			{
				IntVec3 below = bands.Translate(cur, band);
				if (!below.InBounds(map) || bands.InGutter(below))
				{
					return false;
				}
				TerrainDef f = map.terrainGrid.FoundationAt(below);
				if (f != null && f.IsSubstructure)
				{
					return true;
				}
				if (!IsDeckBase(BaseLayer(map, below)))
				{
					return false;
				}
				cur = below;
			}
			return false;
		}

		public static bool IsShipStructure(Map map, IntVec3 c)
		{
			TerrainDef f = map.terrainGrid.FoundationAt(c);
			if (f != null && f.IsSubstructure)
			{
				return true;
			}
			return BaseLayer(map, c) == ABDefOf.AB_RoofSurface;
		}

		internal static bool SetStaticBool(FieldInfo f, bool value)
		{
			if (f == null || (bool)f.GetValue(null) == value)
			{
				return false;
			}
			f.SetValue(null, value);
			return true;
		}

		internal static List<Building_ABStairs2> Counterparts(Building_ABStairs2 s)
		{
			return F_counterparts.GetValue(s) as List<Building_ABStairs2>;
		}

		// ---------------------------------------------------------------- upper deck shape

		/// <summary>
		/// Every cell above the ship that the ship holds up, with its level (1 = one level up).
		/// A cell on level k belongs to the ship when the cell under it does and its ground is a
		/// rooftop or wall top (the ground AASB2 derives from a roof or wall below).
		/// </summary>
		public static Dictionary<IntVec3, int> ComputeUpper(Map map, int shipBand, IEnumerable<IntVec3> lower)
		{
			Dictionary<IntVec3, int> result = new Dictionary<IntVec3, int>();
			ABBandMap bands = Bands(map);
			if (bands == null || lower == null)
			{
				return result;
			}
			List<IntVec3> prev = new List<IntVec3>(lower);
			for (int k = 1; shipBand + k < bands.bandCount && prev.Count > 0; k++)
			{
				int band = shipBand + k;
				List<IntVec3> next = new List<IntVec3>();
				foreach (IntVec3 c in prev)
				{
					IntVec3 u = bands.Translate(c, band);
					if (!u.InBounds(map) || bands.InGutter(u) || result.ContainsKey(u))
					{
						continue;
					}
					if (!IsDeckBase(BaseLayer(map, u)))
					{
						continue;
					}
					result.Add(u, k);
					next.Add(u);
				}
				prev = next;
			}
			return result;
		}

		private sealed class UpperCache
		{
			public int tick = int.MinValue;

			public int lowerCount = -1;

			public readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
		}

		private static readonly ConditionalWeakTable<Building_GravEngine, UpperCache> upperCaches = new ConditionalWeakTable<Building_GravEngine, UpperCache>();

		private static readonly HashSet<IntVec3> NoCells = new HashSet<IntVec3>();

		private const int UpperCacheTicks = 60;

		/// <summary>The engine's upper deck right now, cached for a second of game time.</summary>
		public static HashSet<IntVec3> UpperCells(Building_GravEngine engine)
		{
			if (!Ready || engine == null || !engine.Spawned)
			{
				return NoCells;
			}
			Map map = engine.Map;
			ABBandMap bands = Bands(map);
			if (bands == null)
			{
				return NoCells;
			}
			UpperCache cache = upperCaches.GetValue(engine, _ => new UpperCache());
			HashSet<IntVec3> lower = engine.ValidSubstructureNoRegen;
			int now = Find.TickManager?.TicksGame ?? 0;
			if (cache.lowerCount == lower.Count && now >= cache.tick && now - cache.tick < UpperCacheTicks)
			{
				return cache.cells;
			}
			cache.cells.Clear();
			foreach (IntVec3 c in ComputeUpper(map, bands.BandOf(engine.Position), lower).Keys)
			{
				cache.cells.Add(c);
			}
			cache.tick = now;
			cache.lowerCount = lower.Count;
			return cache.cells;
		}

		private sealed class ShipCellsCache
		{
			public int upperTick = int.MinValue;

			public int lowerCount = -1;

			public int upperCount = -1;

			public readonly HashSet<IntVec3> cells = new HashSet<IntVec3>();
		}

		private static readonly ConditionalWeakTable<Building_GravEngine, ShipCellsCache> shipCellsCaches = new ConditionalWeakTable<Building_GravEngine, ShipCellsCache>();

		/// <summary>
		/// The main deck plus every upper deck. Replaces engine.ValidSubstructure in checks that
		/// read it directly to ask whether a pawn is on the ship (the launch ritual, boarding).
		/// </summary>
		public static HashSet<IntVec3> ShipCells(Building_GravEngine engine)
		{
			HashSet<IntVec3> lower = engine.ValidSubstructure;
			HashSet<IntVec3> upper = UpperCells(engine);
			if (upper.Count == 0)
			{
				return lower;
			}
			ShipCellsCache cache = shipCellsCaches.GetValue(engine, _ => new ShipCellsCache());
			int upperTick = upperCaches.TryGetValue(engine, out UpperCache uc) ? uc.tick : 0;
			if (cache.upperTick != upperTick || cache.lowerCount != lower.Count || cache.upperCount != upper.Count)
			{
				cache.cells.Clear();
				cache.cells.UnionWith(lower);
				cache.cells.UnionWith(upper);
				cache.upperTick = upperTick;
				cache.lowerCount = lower.Count;
				cache.upperCount = upper.Count;
			}
			return cache.cells;
		}

		public static bool IsUpperCell(Building_GravEngine engine, IntVec3 c)
		{
			if (!Ready || engine == null)
			{
				return false;
			}
			LaunchCtx L = launch;
			if (L != null && L.engine == engine)
			{
				return L.upper.ContainsKey(c);
			}
			if (!engine.Spawned)
			{
				return false;
			}
			ABBandMap bands = Bands(engine.Map);
			if (bands == null || !c.InBounds(engine.Map) || bands.BandOf(c) <= bands.BandOf(engine.Position))
			{
				return false;
			}
			return UpperCells(engine).Contains(c);
		}

		/// <summary>OnValidSubstructure, counting the upper deck as part of the ship.</summary>
		public static bool ThingOnShip(Building_GravEngine engine, Thing thing)
		{
			LaunchCtx L = launch;
			HashSet<IntVec3> lower = (L != null && L.engine == engine) ? L.capture : engine.ValidSubstructureNoRegen;
			if (lower == null)
			{
				return false;
			}
			BuildingProperties building = thing.def.building;
			if (building != null && building.isAttachment)
			{
				Thing wall = GenConstruct.GetWallAttachedTo(thing);
				return wall != null && (lower.Contains(wall.Position) || IsUpperCell(engine, wall.Position));
			}
			foreach (IntVec3 c in thing.OccupiedRect())
			{
				if (!lower.Contains(c) && !IsUpperCell(engine, c))
				{
					return false;
				}
			}
			return true;
		}

		// ---------------------------------------------------------------- launch

		internal sealed class LaunchCtx
		{
			public Building_GravEngine engine;

			public Map map;

			public ABBandMap bands;

			public int shipBand;

			public HashSet<IntVec3> capture;

			public Dictionary<IntVec3, int> upper;

			public readonly Dictionary<Building_ABStairs2, List<Building_ABStairs2>> stairLinks = new Dictionary<Building_ABStairs2, List<Building_ABStairs2>>();

			public bool syncSuspended;
		}

		internal static LaunchCtx launch;

		/// <summary>
		/// Replaces engine.ValidSubstructure where the ship is captured: the main deck plus the
		/// upper decks, but only for the engine that is launching right now.
		/// </summary>
		public static HashSet<IntVec3> CaptureSet(Building_GravEngine engine)
		{
			LaunchCtx L = launch;
			if (L != null && L.engine == engine)
			{
				return L.capture;
			}
			return engine.ValidSubstructure;
		}

		/// <summary>GravshipUtility.GenerateGravship prefix.</summary>
		internal static void BeginLaunch(Building_GravEngine engine)
		{
			CleanupLaunch();
			place = null;
			if (!Ready || engine == null || !engine.Spawned)
			{
				return;
			}
			Map map = engine.Map;
			ABBandMap bands = Bands(map);
			if (bands == null)
			{
				return;
			}
			engine.ForceSubstructureDirty();
			HashSet<IntVec3> lower = engine.ValidSubstructure;
			int shipBand = bands.BandOf(engine.Position);
			Dictionary<IntVec3, int> upper = ComputeUpper(map, shipBand, lower);
			if (upper.Count == 0)
			{
				MDGLog.Message("launch: no upper deck above " + engine.RenamableLabel + " (" + lower.Count + " main deck cells).");
				return;
			}
			LaunchCtx L = new LaunchCtx
			{
				engine = engine,
				map = map,
				bands = bands,
				shipBand = shipBand,
				upper = upper,
				capture = new HashSet<IntVec3>(lower)
			};
			foreach (IntVec3 c in upper.Keys)
			{
				L.capture.Add(c);
			}
			// Remember every stair link before anything despawns: AASB2's DeSpawn clears both ends.
			foreach (IntVec3 c in L.capture)
			{
				List<Thing> list = map.thingGrid.ThingsListAtFast(c);
				for (int i = 0; i < list.Count; i++)
				{
					if (list[i] is Building_ABStairs2 s && !L.stairLinks.ContainsKey(s))
					{
						L.stairLinks[s] = new List<Building_ABStairs2>(Counterparts(s) ?? new List<Building_ABStairs2>());
					}
				}
			}
			L.syncSuspended = SetStaticBool(F_syncSuspended, true);
			launch = L;
			MDGLog.Dev("launch of " + engine.RenamableLabel + ": " + lower.Count + " main deck cells, " + upper.Count + " upper deck cells on " + upper.Values.Max() + " level(s), " + L.stairLinks.Count + " stair piece(s).");
		}

		/// <summary>Gravship constructor postfix: re-key the captured upper-deck offsets.</summary>
		internal static void ConvertCapturedOffsets(Gravship g, Building_GravEngine engine)
		{
			LaunchCtx L = launch;
			if (L == null || L.engine != engine || g == null)
			{
				return;
			}
			IntVec3 origin = g.originalPosition;
			Func<IntVec3, IntVec3> conv = o => ToDeckOffset(L, origin, o);
			int moved = 0;
			moved += RetargetPositions(G_things(g), conv);
			RetargetPositions(G_pawns(g), conv);
			RetargetPositions(G_thrusters(g), conv);
			Dictionary<Thing, PositionData> doors = G_exteriorDoors(g);
			RetargetPositions(doors, conv);
			// Exterior doors only feed the ghost drawn on the landing marker, which shows the main deck.
			foreach (Thing door in doors.Where(kv => kv.Value.position.y != 0).Select(kv => kv.Key).ToList())
			{
				doors.Remove(door);
			}
			RekeyDict(G_roofs(g), conv, dropNull: false);
			RekeyDict(G_foundations(g), conv, dropNull: true);
			RekeyDict(G_terrains(g), conv, dropNull: false);
			RekeyDict(G_terrainColors(g), conv, dropNull: false);
			RekeyDict(G_terrainDesignations(g), conv, dropNull: false);
			RekeyDict(G_gases(g), conv, dropNull: false);
			foreach (RoomTemperatureVacuum r in G_roomTemperatures(g))
			{
				r.roomCell = conv(r.roomCell);
			}
			List<IntVec3> extenders = G_gravFieldExtenderPositions(g);
			for (int i = 0; i < extenders.Count; i++)
			{
				extenders[i] = conv(extenders[i]);
			}
			foreach (MoveableArea area in AllAreas(g.areas))
			{
				List<IntVec3> cells = A_relativeCells(area);
				for (int i = 0; i < cells.Count; i++)
				{
					cells[i] = conv(cells[i]);
				}
			}
			// The footprint (bounds, occupied rects) must be the main deck only; y != 0 cells never
			// match UpdateBoundingBoxes' (x, 0, z) probes, and FromCellList ignores y.
			G_occupiedRects(g).Clear();
			G_bounds(g) = CellRect.Empty;
			M_UpdateBoundingBoxes.Invoke(g, new object[] { new HashSet<IntVec3>(G_terrains(g).Keys) });
			// Vanilla Gravship Expanded keeps vac-barrier roof colours in a side table filled from
			// the same raw offsets; its WorldObjectComp picks the table up after this constructor.
			if (F_vgeTempVacColors != null && F_vgeCurrentGravship != null && F_vgeCurrentGravship.GetValue(null) == g)
			{
				if (F_vgeTempVacColors.GetValue(null) is Dictionary<IntVec3, Color> vac)
				{
					RekeyDict(vac, conv, dropNull: false);
				}
			}
			List<KeyValuePair<Thing, PositionData>> upperThings = G_things(g).Where(kv => kv.Value != null && kv.Value.position.y != 0).ToList();
			int big = upperThings.Count(kv => kv.Key.def.size != IntVec2.One);
			MDGLog.Message("launch: " + L.upper.Count + " upper deck cells, " + upperThings.Count + " upper deck objects captured (" + big + " larger than one cell), " + G_pawns(g).Values.Count(pd => pd != null && pd.position.y != 0) + " pawns upstairs.");
			foreach (KeyValuePair<Thing, PositionData> kv in upperThings.Where(kv => kv.Key.def.size != IntVec2.One))
			{
				MDGLog.Dev("  upper " + kv.Key.def.defName + " " + kv.Key.def.size + " at " + kv.Key.Position + " -> offset " + kv.Value.position);
			}
		}

		private static IntVec3 ToDeckOffset(LaunchCtx L, IntVec3 origin, IntVec3 o)
		{
			if (o.y != 0)
			{
				return o;
			}
			IntVec3 abs = new IntVec3(origin.x + o.x, 0, origin.z + o.z);
			if (!abs.InBounds(L.map))
			{
				return o;
			}
			int band = L.bands.BandOf(abs);
			if (band == L.shipBand)
			{
				return o;
			}
			IntVec3 inBand = L.bands.Translate(abs, L.shipBand);
			return new IntVec3(inBand.x - origin.x, band - L.shipBand, inBand.z - origin.z);
		}

		private static int RetargetPositions<T>(Dictionary<T, PositionData> dict, Func<IntVec3, IntVec3> conv)
		{
			int n = 0;
			if (dict == null)
			{
				return 0;
			}
			foreach (PositionData pd in dict.Values)
			{
				if (pd == null)
				{
					continue;
				}
				IntVec3 c = conv(pd.position);
				if (c != pd.position)
				{
					pd.position = c;
					n++;
				}
			}
			return n;
		}

		private static void RekeyDict<T>(Dictionary<IntVec3, T> dict, Func<IntVec3, IntVec3> conv, bool dropNull)
		{
			if (dict == null || dict.Count == 0)
			{
				return;
			}
			List<KeyValuePair<IntVec3, T>> entries = dict.ToList();
			dict.Clear();
			foreach (KeyValuePair<IntVec3, T> kv in entries)
			{
				if (dropNull && kv.Value == null)
				{
					continue;
				}
				dict[conv(kv.Key)] = kv.Value;
			}
		}

		internal static IEnumerable<MoveableArea> AllAreas(MoveableAreas areas)
		{
			if (areas == null)
			{
				yield break;
			}
			foreach (MoveableStockpile s in areas.stockpiles)
			{
				if (s != null)
				{
					yield return s;
				}
			}
			foreach (MoveableGrowZone z in areas.growZones)
			{
				if (z != null)
				{
					yield return z;
				}
			}
			foreach (MoveableArea_Allowed a in areas.allowedAreas)
			{
				if (a != null)
				{
					yield return a;
				}
			}
			MoveableArea[] singles = { areas.homeArea, areas.buildRoofArea, areas.noRoofArea, areas.snowClearArea, areas.pollutionClearArea };
			foreach (MoveableArea a in singles)
			{
				if (a != null)
				{
					yield return a;
				}
			}
		}

		/// <summary>GravshipUtility.GenerateGravship postfix: fix up what the vanilla removal left.</summary>
		internal static void FinishLaunch(Gravship g)
		{
			LaunchCtx L = launch;
			if (L == null)
			{
				return;
			}
			try
			{
				if (g != null)
				{
					RestoreStairs(L, g);
				}
				ClearVacatedDeck(L);
			}
			catch (Exception e)
			{
				MDGLog.Error("tidying the old map after launch failed: " + e);
			}
		}

		/// <summary>GravshipUtility.GenerateGravship finalizer: always drop the launch state.</summary>
		internal static void CleanupLaunch()
		{
			LaunchCtx L = launch;
			if (L == null)
			{
				return;
			}
			launch = null;
			if (L.syncSuspended)
			{
				SetStaticBool(F_syncSuspended, false);
			}
		}

		private static void RestoreStairs(LaunchCtx L, Gravship g)
		{
			HashSet<Thing> toDestroy = new HashSet<Thing>();
			foreach (KeyValuePair<Building_ABStairs2, List<Building_ABStairs2>> kv in L.stairLinks)
			{
				Building_ABStairs2 s = kv.Key;
				if (g.ContainsThing(s))
				{
					List<Building_ABStairs2> list = Counterparts(s);
					if (list == null)
					{
						continue;
					}
					list.Clear();
					foreach (Building_ABStairs2 cp in kv.Value)
					{
						if (cp == null)
						{
							continue;
						}
						if (g.ContainsThing(cp))
						{
							if (!list.Contains(cp))
							{
								list.Add(cp);
							}
						}
						else if (cp.Spawned)
						{
							// The other end stays on the old map (a basement ladder, say): the shaft collapses as AASB2 would.
							toDestroy.Add(cp);
						}
					}
				}
				else if (s.Spawned && kv.Value.Any(cp => cp != null && g.ContainsThing(cp)))
				{
					toDestroy.Add(s);
				}
			}
			foreach (Thing t in toDestroy)
			{
				if (t.Spawned && !t.Destroyed)
				{
					t.Destroy(DestroyMode.Vanish);
				}
			}
			MDGLog.Dev("stairs: kept " + L.stairLinks.Keys.Count(s => g.ContainsThing(s)) + " piece(s) linked on the ship, collapsed " + toDestroy.Count + " left-behind end(s).");
		}

		/// <summary>The upper deck left with the ship: turn its cells back into open air.</summary>
		private static void ClearVacatedDeck(LaunchCtx L)
		{
			Map map = L.map;
			TerrainDef air = ABDefOf.AB_OpenAir;
			int cleared = 0;
			int kept = 0;
			foreach (KeyValuePair<IntVec3, int> kv in L.upper.OrderBy(kv => kv.Value))
			{
				IntVec3 c = kv.Key;
				if (!c.InBounds(map) || !IsDerived(BaseLayer(map, c)))
				{
					continue;
				}
				List<Thing> things = map.thingGrid.ThingsListAt(c);
				bool holds = false;
				for (int i = things.Count - 1; i >= 0; i--)
				{
					Thing t = things[i];
					ThingCategory cat = t.def.category;
					if (cat == ThingCategory.Building || cat == ThingCategory.Pawn || cat == ThingCategory.Item)
					{
						holds = true;
					}
					else if ((cat == ThingCategory.Filth || cat == ThingCategory.Plant) && t.def.destroyable && !t.Destroyed)
					{
						t.Destroy(DestroyMode.Vanish);
					}
				}
				if (holds)
				{
					// Something did not fit on the ship and stayed behind; like AASB2, never pull the floor from under it.
					kept++;
					continue;
				}
				if (map.terrainGrid.TerrainAt(c) != air)
				{
					map.terrainGrid.SetTerrain(c, air);
				}
				cleared++;
			}
			L.bands.DirtySeeBelowCache();
			MDGLog.Dev("old map: " + cleared + " upper deck cell(s) back to open air" + ((kept > 0) ? (", " + kept + " kept under things left behind") : "") + ".");
		}

		// ---------------------------------------------------------------- landing

		internal sealed class PlaceCtx
		{
			public Gravship gravship;

			public Map map;

			public ABBandMap bands;

			public IntVec3 root;

			public int shipBand;

			public readonly List<Pawn> lowered = new List<Pawn>();

			public readonly List<IntVec3> lowerCells = new List<IntVec3>();

			public readonly List<IntVec3> upperCells = new List<IntVec3>();

			public int stowed;

			public int deployed;

			public bool syncSuspended;

			public bool Fits(int level)
			{
				return bands != null && shipBand + level >= 0 && shipBand + level < bands.bandCount;
			}
		}

		internal static PlaceCtx place;

		private static int addWarnings;

		/// <summary>
		/// Replaces IntVec3 + IntVec3 wherever a gravship offset becomes a map cell. With no
		/// level in either operand it is exactly vanilla's addition.
		/// </summary>
		public static IntVec3 Add(IntVec3 a, IntVec3 b)
		{
			if (a.y == 0 && b.y == 0)
			{
				return new IntVec3(a.x + b.x, 0, a.z + b.z);
			}
			if (a.y != 0 && b.y == 0)
			{
				IntVec3 t = a;
				a = b;
				b = t;
			}
			IntVec3 flat = new IntVec3(a.x + b.x, a.y, a.z + b.z);
			PlaceCtx P = place;
			if (P == null || P.bands == null || !P.Fits(b.y))
			{
				if (addWarnings++ < 5)
				{
					MDGLog.Warning("an upper deck offset " + b + " was placed with no level to go to; using the main deck cell " + flat + ".");
				}
				return flat;
			}
			return P.bands.Translate(flat, P.shipBand + b.y);
		}

		internal static bool HasUpperEntries(Gravship g)
		{
			return G_terrains(g).Keys.Any(k => k.y != 0) || G_things(g).Values.Any(pd => pd != null && pd.position.y != 0) || G_pawns(g).Values.Any(pd => pd != null && pd.position.y != 0);
		}

		/// <summary>GravshipPlacementUtility.PlaceGravshipInMap prefix.</summary>
		internal static void BeginPlace(Gravship g, IntVec3 root, Map map)
		{
			place = null;
			if (!Ready || g == null || map == null)
			{
				return;
			}
			StowedDeck stow = DeckStowage.Get(g.Engine, create: false);
			bool hasStow = stow != null && !stow.Empty;
			if (!hasStow && !HasUpperEntries(g))
			{
				return;
			}
			ABBandMap bands = Bands(map);
			PlaceCtx P = new PlaceCtx
			{
				gravship = g,
				map = map,
				bands = bands,
				root = root,
				shipBand = (bands != null) ? bands.BandOf(root) : 0
			};
			place = P;
			if (hasStow)
			{
				Deploy(g, stow, P);
			}
			Stow(g, P);
			ResetPlacementCaches(g);
			PrepareUpperCells(g, P);
			MDGLog.Message("landing " + g.Label + " at " + root + ((bands != null) ? (" (band " + P.shipBand + " of " + bands.bandCount + ")") : " (map has no levels)") + ": " + P.upperCells.Count + " upper deck cell(s), " + P.deployed + " unfolded, " + P.stowed + " folded away, " + P.lowered.Count + " pawn(s) set down.");
		}

		private static void ResetPlacementCaches(Gravship g)
		{
			foreach (FieldInfo f in F_placementCaches)
			{
				f.SetValue(g, null);
			}
		}

		private static void PrepareUpperCells(Gravship g, PlaceCtx P)
		{
			if (P.bands == null)
			{
				return;
			}
			Map map = P.map;
			List<KeyValuePair<IntVec3, TerrainDef>> upper = new List<KeyValuePair<IntVec3, TerrainDef>>();
			foreach (KeyValuePair<IntVec3, TerrainDef> kv in g.Terrains)
			{
				if (kv.Key.y == 0)
				{
					P.lowerCells.Add(Add(P.root, kv.Key));
				}
				else if (P.Fits(kv.Key.y))
				{
					upper.Add(kv);
				}
			}
			if (upper.Count == 0)
			{
				return;
			}
			P.syncSuspended = SetStaticBool(F_syncSuspended, true);
			// AASB2 nudges unowned "decorations" spawned off the surface band during the tick a map
			// was generated; a landing on a fresh map can fall in that tick, and items have no faction.
			if (F_decorWindowMap != null)
			{
				F_decorWindowMap.SetValue(null, null);
			}
			HashSet<int> bandsOpened = new HashSet<int>();
			foreach (KeyValuePair<IntVec3, TerrainDef> kv in upper)
			{
				IntVec3 c = Add(P.root, kv.Key);
				if (!c.InBounds(map))
				{
					continue;
				}
				P.upperCells.Add(c);
				ClearForDeck(map, c);
				// Lay the ground the deck stands on before vanilla lays floors and spawns things on it.
				TerrainDef want = (kv.Value == ABDefOf.AB_WallTop) ? ABDefOf.AB_WallTop : ABDefOf.AB_RoofSurface;
				if (map.terrainGrid.TerrainAt(c) != want || map.terrainGrid.UnderTerrainAt(c) != null)
				{
					map.terrainGrid.SetTerrain(c, want);
				}
				map.fogGrid.Unfog(c);
				int band = P.bands.BandOf(c);
				if (bandsOpened.Add(band))
				{
					P.bands.Open(band);
				}
			}
			P.bands.DirtySeeBelowCache();
		}

		private static void ClearForDeck(Map map, IntVec3 c)
		{
			List<Thing> list = map.thingGrid.ThingsListAt(c);
			for (int i = list.Count - 1; i >= 0; i--)
			{
				Thing t = list[i];
				if (!t.def.destroyable || t.Destroyed)
				{
					continue;
				}
				ThingCategory cat = t.def.category;
				if (cat == ThingCategory.Plant || cat == ThingCategory.Filth || (t.def.building != null && t.def.building.isNaturalRock))
				{
					t.Destroy(DestroyMode.Vanish);
				}
			}
			map.snowGrid.SetDepth(c, 0f);
			map.sandGrid?.SetDepth(c, 0f);
		}

		/// <summary>Fold away whatever has no level to go to on this map.</summary>
		private static void Stow(Gravship g, PlaceCtx P)
		{
			Dictionary<Thing, PositionData> things = G_things(g);
			List<KeyValuePair<Thing, PositionData>> outThings = things.Where(kv => kv.Value != null && kv.Value.position.y != 0 && !P.Fits(kv.Value.position.y)).ToList();
			bool outTerrain = G_terrains(g).Keys.Any(k => k.y != 0 && !P.Fits(k.y));
			bool outPawns = G_pawns(g).Values.Any(pd => pd != null && pd.position.y != 0 && !P.Fits(pd.position.y));
			if (outThings.Count == 0 && !outTerrain && !outPawns)
			{
				return;
			}
			StowedDeck sd = DeckStowage.Get(g.Engine, create: true);
			HashSet<Thing> stowedSet = new HashSet<Thing>();
			foreach (KeyValuePair<Thing, PositionData> kv in outThings)
			{
				Thing t = kv.Key;
				things.Remove(t);
				G_powerOn(g).Remove(t);
				G_connectParents(g).Remove(t);
				G_thrusters(g).Remove(t);
				G_exteriorDoors(g).Remove(t);
				sd.Add(t, kv.Value);
				stowedSet.Add(t);
			}
			Dictionary<Thing, Thing> parents = G_connectParents(g);
			foreach (Thing child in parents.Where(kv => kv.Value != null && stowedSet.Contains(kv.Value)).Select(kv => kv.Key).ToList())
			{
				parents.Remove(child);
			}
			if (g.areas != null)
			{
				foreach (MoveableStorageGroup group in g.areas.storageGroups)
				{
					group.members.RemoveAll(m => m is Thing mt && stowedSet.Contains(mt));
				}
				g.areas.storageGroups.RemoveAll(group => group.members.Count == 0);
			}
			foreach (KeyValuePair<Pawn, PositionData> kv in G_pawns(g))
			{
				PositionData pd = kv.Value;
				if (pd != null && pd.position.y != 0 && !P.Fits(pd.position.y))
				{
					pd.position = new IntVec3(pd.position.x, 0, pd.position.z);
					P.lowered.Add(kv.Key);
				}
			}
			MoveOut(G_terrains(g), sd.terrains, P);
			MoveOut(G_roofs(g), sd.roofs, P);
			MoveOut(G_terrainColors(g), sd.colors, P);
			MoveOut(G_foundations(g), null, P);
			MoveOut(G_gases(g), null, P);
			MoveOut(G_terrainDesignations(g), null, P);
			G_roomTemperatures(g).RemoveAll(r => r.roomCell.y != 0 && !P.Fits(r.roomCell.y));
			G_gravFieldExtenderPositions(g).RemoveAll(c => c.y != 0 && !P.Fits(c.y));
			foreach (MoveableArea area in AllAreas(g.areas))
			{
				A_relativeCells(area).RemoveAll(c => c.y != 0 && !P.Fits(c.y));
			}
			if (g.areas != null)
			{
				g.areas.stockpiles.RemoveAll(s => s == null || A_relativeCells(s).Count == 0);
				g.areas.growZones.RemoveAll(z => z == null || A_relativeCells(z).Count == 0);
			}
			P.stowed += outThings.Count;
		}

		private static void MoveOut<T>(Dictionary<IntVec3, T> from, Dictionary<IntVec3, T> to, PlaceCtx P)
		{
			if (from == null)
			{
				return;
			}
			foreach (IntVec3 k in from.Keys.Where(k => k.y != 0 && !P.Fits(k.y)).ToList())
			{
				if (to != null)
				{
					to[k] = from[k];
				}
				from.Remove(k);
			}
		}

		/// <summary>Put a folded deck back on the ship when this map has room for it.</summary>
		private static void Deploy(Gravship g, StowedDeck sd, PlaceCtx P)
		{
			Dictionary<Thing, PositionData> things = G_things(g);
			int n = 0;
			for (int i = sd.things.Count - 1; i >= 0; i--)
			{
				Thing t = sd.things[i];
				PositionData pd = sd.positions[i];
				if (t == null || t.Destroyed || pd == null)
				{
					sd.RemoveAt(i);
					continue;
				}
				if (!P.Fits(pd.position.y))
				{
					continue;
				}
				if (!things.ContainsKey(t))
				{
					things[t] = pd;
					n++;
				}
				sd.RemoveAt(i);
			}
			MoveIn(sd.terrains, G_terrains(g), P);
			MoveIn(sd.roofs, G_roofs(g), P);
			MoveIn(sd.colors, G_terrainColors(g), P);
			P.deployed += n;
			if (sd.Empty)
			{
				DeckStowage.Remove(g.Engine);
			}
		}

		private static void MoveIn<T>(Dictionary<IntVec3, T> from, Dictionary<IntVec3, T> to, PlaceCtx P)
		{
			foreach (IntVec3 k in from.Keys.Where(k => P.Fits(k.y)).ToList())
			{
				if (!to.ContainsKey(k))
				{
					to[k] = from[k];
				}
				from.Remove(k);
			}
		}

		/// <summary>GravshipPlacementUtility.PlaceGravshipInMap postfix.</summary>
		internal static void FinishPlace(Gravship g)
		{
			PlaceCtx P = place;
			if (P == null || P.gravship != g)
			{
				return;
			}
			try
			{
				RelinkStairs(P);
				VerifyUpperPlacement(g, P);
				int homed = ExtendHomeToDecks(P.map, P.bands, P.shipBand, P.upperCells);
				if (homed > 0)
				{
					MDGLog.Dev("added " + homed + " upper deck cell(s) to the home area.");
				}
				foreach (Pawn p in P.lowered)
				{
					ReseatPawn(p);
				}
				LookTargets look = (g.Engine != null && g.Engine.Spawned) ? new LookTargets(g.Engine) : LookTargets.Invalid;
				if (P.stowed > 0)
				{
					Messages.Message("MDG_DeckStowed".Translate(g.Label, P.stowed), look, MessageTypeDefOf.NeutralEvent, historical: false);
				}
				if (P.deployed > 0)
				{
					Messages.Message("MDG_DeckDeployed".Translate(g.Label, P.deployed), look, MessageTypeDefOf.PositiveEvent, historical: false);
				}
			}
			catch (Exception e)
			{
				MDGLog.Error("finishing the upper deck after landing failed: " + e);
			}
			finally
			{
				ResumeSync(P, reconcile: true);
			}
		}

		/// <summary>PlaceGravshipInMap finalizer: never leave AASB2's roof sync switched off.</summary>
		internal static void PlaceFinalizer()
		{
			PlaceCtx P = place;
			if (P != null)
			{
				ResumeSync(P, reconcile: false);
			}
		}

		private static void ResumeSync(PlaceCtx P, bool reconcile)
		{
			if (!P.syncSuspended)
			{
				return;
			}
			P.syncSuspended = false;
			SetStaticBool(F_syncSuspended, false);
			if (!reconcile || P.bands == null)
			{
				return;
			}
			if (ABOrbitRulesInVacuum(P.map) && (MDGMod.Settings == null || !MDGMod.Settings.orbitDecks))
			{
				return;
			}
			try
			{
				// Now that roofs and walls are in, let AASB2 derive the rooftop above the top deck.
				foreach (IntVec3 c in P.lowerCells)
				{
					ABSkySync.SyncAbove(P.map, c);
				}
				foreach (IntVec3 c in P.upperCells)
				{
					ABSkySync.SyncAbove(P.map, c);
				}
			}
			catch (Exception e)
			{
				MDGLog.Warning("rooftop sync after landing failed (the deck is fine; rooftops above it may be missing): " + e.Message);
			}
		}

		private static bool ABOrbitRulesInVacuum(Map map)
		{
			return ModsConfig.OdysseyActive && map?.Biome != null && map.Biome.inVacuum;
		}

		/// <summary>Clears the landing context once the controller is done with it.</summary>
		internal static void EndPlace()
		{
			PlaceCtx P = place;
			if (P != null)
			{
				ResumeSync(P, reconcile: false);
			}
			place = null;
		}

		/// <summary>After landing, count upper-deck things that did not end up on their own level.</summary>
		/// <summary>
		/// Upper-deck cells join the home area wherever the main-deck cell under them is home, so
		/// colonists restricted to Home still fetch food and work upstairs. Only adds, never removes.
		/// </summary>
		public static int ExtendHomeToDecks(Map map, ABBandMap bands, int shipBand, IEnumerable<IntVec3> upperCells)
		{
			if (MDGMod.Settings == null || !MDGMod.Settings.homeOnDecks || map?.areaManager?.Home == null || bands == null || upperCells == null)
			{
				return 0;
			}
			Area_Home home = map.areaManager.Home;
			int added = 0;
			foreach (IntVec3 c in upperCells)
			{
				if (!c.InBounds(map) || home[c])
				{
					continue;
				}
				IntVec3 below = bands.Translate(c, shipBand);
				if (below.InBounds(map) && home[below])
				{
					home[c] = true;
					added++;
				}
			}
			return added;
		}

		private static void VerifyUpperPlacement(Gravship g, PlaceCtx P)
		{
			if (P.bands == null)
			{
				return;
			}
			int ok = 0;
			int wrong = 0;
			foreach (KeyValuePair<Thing, PositionData> kv in G_things(g))
			{
				Thing t = kv.Key;
				PositionData pd = kv.Value;
				if (pd == null || pd.position.y == 0 || !P.Fits(pd.position.y) || t == null || !t.Spawned || t.Map != P.map)
				{
					continue;
				}
				int want = P.shipBand + pd.position.y;
				int got = P.bands.BandOf(t.Position);
				if (got == want)
				{
					ok++;
					continue;
				}
				wrong++;
				if (wrong <= 10)
				{
					MDGLog.Warning("upper-deck " + t.def.defName + " " + t.def.size + " ended on level " + (got - P.bands.surfaceBand) + " at " + t.Position + " instead of level " + (want - P.bands.surfaceBand) + ". Please report this with the log.");
				}
			}
			MDGLog.Message("landing check: " + ok + " upper-deck object(s) on their level" + ((wrong > 0) ? (", " + wrong + " NOT") : "") + ".");
		}

		private static void RelinkStairs(PlaceCtx P)
		{
			int linked = 0;
			int rebuilt = 0;
			foreach (Thing t in P.gravship.Things.ToList())
			{
				if (!(t is Building_ABStairs2 s) || !s.Spawned)
				{
					continue;
				}
				List<Building_ABStairs2> list = Counterparts(s);
				if (list == null)
				{
					continue;
				}
				list.RemoveAll(cp => cp == null || cp.Destroyed);
				bool any = false;
				bool pending = false;
				foreach (Building_ABStairs2 cp in list.ToList())
				{
					if (cp.Spawned && cp.Map == s.Map)
					{
						ABWormhole.Link(s, cp);
						any = true;
						if (P.bands != null)
						{
							P.bands.Open(P.bands.BandOf(cp.Position));
						}
					}
					else if (!cp.Spawned)
					{
						pending = true;
					}
				}
				if (any)
				{
					linked++;
				}
				else if (!pending && P.bands != null)
				{
					// The other end did not come along (it went to a basement on the old map): build a new one.
					M_TryEstablish.Invoke(s, new object[] { s.Map });
					rebuilt++;
				}
			}
			MDGLog.Dev("stairs after landing: " + linked + " piece(s) linked, " + rebuilt + " rebuilt.");
		}

		private static void ReseatPawn(Pawn p)
		{
			if (p == null || !p.Spawned || p.Position.Standable(p.Map))
			{
				return;
			}
			Map map = p.Map;
			if (CellFinder.TryFindRandomCellNear(p.Position, map, 12, c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
			{
				p.Position = cell;
				p.Notify_Teleported();
			}
		}

		// ---------------------------------------------------------------- landing checks

		/// <summary>The upper decks must find empty sky (or a bare rooftop) on the levels above the landing spot.</summary>
		internal static AcceptanceReport CheckUpperLanding(Gravship g, IntVec3 root, Map map, List<IntVec3> validCells, List<IntVec3> invalidCells)
		{
			ABBandMap bands = Bands(map);
			if (!Ready || bands == null || g == null)
			{
				return true;
			}
			int shipBand = bands.BandOf(root);
			AcceptanceReport result = true;
			bool collect = validCells != null || invalidCells != null;
			IEnumerable<IntVec3> offsets = g.Terrains.Keys.Where(k => k.y != 0);
			StowedDeck sd = DeckStowage.Get(g.Engine, create: false);
			if (sd != null && sd.terrains.Count > 0)
			{
				Rot4 rot = g.Rotation;
				offsets = offsets.Concat(sd.terrains.Keys.Select(k => PrefabUtility.GetAdjustedLocalPosition(k, rot)));
			}
			foreach (IntVec3 off in offsets)
			{
				int band = shipBand + off.y;
				if (band < 0 || band >= bands.bandCount)
				{
					continue;
				}
				IntVec3 flat = new IntVec3(root.x + off.x, 0, root.z + off.z);
				IntVec3 c = bands.Translate(flat, band);
				string reason = UpperBlocked(map, bands, c, off.y);
				if (reason == null)
				{
					continue;
				}
				if (result.Accepted)
				{
					result = new AcceptanceReport(reason);
				}
				if (!collect)
				{
					return result;
				}
				validCells?.Remove(flat);
				if (invalidCells != null && !invalidCells.Contains(flat))
				{
					invalidCells.Add(flat);
				}
			}
			return result;
		}

		private static string UpperBlocked(Map map, ABBandMap bands, IntVec3 c, int level)
		{
			if (!c.InBounds(map) || bands.InGutter(c))
			{
				return "MDG_UpperOutOfBounds".Translate(level);
			}
			TerrainDef ground = BaseLayer(map, c);
			if (!IsDerived(ground))
			{
				return "MDG_UpperBlockedTerrain".Translate(level, (ground != null) ? ground.label : "?");
			}
			List<Thing> things = map.thingGrid.ThingsListAt(c);
			for (int i = 0; i < things.Count; i++)
			{
				Thing t = things[i];
				ThingCategory cat = t.def.category;
				if (cat == ThingCategory.Pawn || cat == ThingCategory.Item || (cat == ThingCategory.Building && t.def.destroyable))
				{
					return "MDG_UpperBlockedBy".Translate(level, t.LabelShort);
				}
			}
			return null;
		}
	}
}
