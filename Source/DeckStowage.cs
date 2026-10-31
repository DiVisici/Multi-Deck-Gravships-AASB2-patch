using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace MultiDeckGravships
{
	/// <summary>An upper deck folded away while its ship sits on a map with no level above.</summary>
	public class StowedDeck : IExposable
	{
		public List<Thing> things = new List<Thing>();

		/// <summary>Unrotated offsets from the grav engine, level in y, parallel to <see cref="things"/>.</summary>
		public List<PositionData> positions = new List<PositionData>();

		public Dictionary<IntVec3, TerrainDef> terrains = new Dictionary<IntVec3, TerrainDef>();

		public Dictionary<IntVec3, RoofDef> roofs = new Dictionary<IntVec3, RoofDef>();

		public Dictionary<IntVec3, ColorDef> colors = new Dictionary<IntVec3, ColorDef>();

		public bool Empty => things.Count == 0 && terrains.Count == 0 && roofs.Count == 0;

		public void Add(Thing t, PositionData pd)
		{
			things.Add(t);
			positions.Add(pd);
		}

		public void RemoveAt(int i)
		{
			things.RemoveAt(i);
			positions.RemoveAt(i);
		}

		public void ExposeData()
		{
			Scribe_Collections.Look(ref things, "things", LookMode.Deep);
			Scribe_Collections.Look(ref positions, "positions", LookMode.Deep);
			Scribe_Collections.Look(ref terrains, "terrains", LookMode.Value, LookMode.Def);
			Scribe_Collections.Look(ref roofs, "roofs", LookMode.Value, LookMode.Def);
			Scribe_Collections.Look(ref colors, "colors", LookMode.Value, LookMode.Def);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				things = things ?? new List<Thing>();
				positions = positions ?? new List<PositionData>();
				terrains = terrains ?? new Dictionary<IntVec3, TerrainDef>();
				roofs = roofs ?? new Dictionary<IntVec3, RoofDef>();
				colors = colors ?? new Dictionary<IntVec3, ColorDef>();
				// Keep the two lists paired even if a def went missing.
				for (int i = things.Count - 1; i >= 0; i--)
				{
					if (things[i] == null || i >= positions.Count || positions[i] == null)
					{
						things.RemoveAt(i);
						if (i < positions.Count)
						{
							positions.RemoveAt(i);
						}
					}
				}
				while (positions.Count > things.Count)
				{
					positions.RemoveAt(positions.Count - 1);
				}
				terrains.RemoveAll(kv => kv.Value == null);
				roofs.RemoveAll(kv => kv.Value == null);
				colors.RemoveAll(kv => kv.Value == null);
			}
		}
	}

	/// <summary>Folded upper decks, keyed by the thing ID of their ship's grav engine.</summary>
	public class DeckStowage : GameComponent
	{
		private Dictionary<int, StowedDeck> decks = new Dictionary<int, StowedDeck>();

		private List<int> tmpKeys;

		private List<StowedDeck> tmpValues;

		public DeckStowage(Game game)
		{
		}

		private static DeckStowage Instance => Current.Game?.GetComponent<DeckStowage>();

		public static StowedDeck Get(Building_GravEngine engine, bool create)
		{
			DeckStowage inst = Instance;
			if (inst == null || engine == null)
			{
				return null;
			}
			if (!inst.decks.TryGetValue(engine.thingIDNumber, out StowedDeck deck) && create)
			{
				deck = new StowedDeck();
				inst.decks[engine.thingIDNumber] = deck;
			}
			return deck;
		}

		public static void Remove(Building_GravEngine engine)
		{
			DeckStowage inst = Instance;
			if (inst != null && engine != null)
			{
				inst.decks.Remove(engine.thingIDNumber);
			}
		}

		public override void ExposeData()
		{
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				decks.RemoveAll(kv => kv.Value == null || kv.Value.Empty);
			}
			Scribe_Collections.Look(ref decks, "decks", LookMode.Value, LookMode.Deep, ref tmpKeys, ref tmpValues);
			if (Scribe.mode == LoadSaveMode.PostLoadInit && decks == null)
			{
				decks = new Dictionary<int, StowedDeck>();
			}
		}
	}
}
