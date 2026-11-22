using System;
using UnityEngine;
using Verse;

namespace MultiDeckGravships
{
	public class MDGSettings : ModSettings
	{
		public bool orbitDecks = true;

		public bool homeOnDecks = true;

		public bool verbose;

		public override void ExposeData()
		{
			Scribe_Values.Look(ref orbitDecks, "orbitDecks", true);
			Scribe_Values.Look(ref homeOnDecks, "homeOnDecks", true);
			Scribe_Values.Look(ref verbose, "verbose", false);
		}
	}

	public class MDGMod : Mod
	{
		public static MDGSettings Settings;

		public MDGMod(ModContentPack content) : base(content)
		{
			Settings = GetSettings<MDGSettings>();
			HarmonyInit.Apply();
		}

		public override string SettingsCategory()
		{
			return "Multi-Deck Gravships";
		}

		public override void DoSettingsWindowContents(Rect inRect)
		{
			Listing_Standard l = new Listing_Standard();
			l.Begin(inRect);
			l.CheckboxLabeled("MDG_OrbitDecks".Translate(), ref Settings.orbitDecks, "MDG_OrbitDecksDesc".Translate());
			l.CheckboxLabeled("MDG_HomeOnDecks".Translate(), ref Settings.homeOnDecks, "MDG_HomeOnDecksDesc".Translate());
			l.CheckboxLabeled("MDG_Verbose".Translate(), ref Settings.verbose, "MDG_VerboseDesc".Translate());
			l.End();
		}
	}

	/// <summary>
	/// Logging goes through here so the offline patch test can run outside Unity.
	/// </summary>
	public static class MDGLog
	{
		public const string Prefix = "[Multi-Deck Gravships] ";

		public static Action<string> MessageSink = s => Log.Message(s);

		public static Action<string> WarningSink = s => Log.Warning(s);

		public static Action<string> ErrorSink = s => Log.Error(s);

		public static bool Verbose => MDGMod.Settings != null && MDGMod.Settings.verbose;

		public static void Message(string s) => MessageSink(Prefix + s);

		public static void Warning(string s) => WarningSink(Prefix + s);

		public static void Error(string s) => ErrorSink(Prefix + s);

		public static void Dev(string s)
		{
			if (Verbose)
			{
				MessageSink(Prefix + s);
			}
		}
	}
}
