﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Comfort.Common;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.Trainer.Configuration;
using EFT.Trainer.Extensions;
using EFT.UI;
using JsonType;
using UnityEngine;
using UnityEngine.SceneManagement;

#nullable enable

namespace EFT.Trainer.Features
{
	internal class Commands : ToggleFeature
	{
		public override string Name => "commands";

		[ConfigurationProperty(Skip = true)] // we do not want to offer save/load support for this
		public override bool Enabled { get; set; } = false;

		[ConfigurationProperty]
		public virtual float X { get; set; } = 40f;

		[ConfigurationProperty]
		public virtual float Y { get; set; } = 20f;

		public override KeyCode Key { get; set; } = KeyCode.RightAlt;

		public bool Registered { get; set; } = false;
		public const string ValueGroup = "value";
		public const string ExtraGroup = "extra";
		public string UserPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Escape from Tarkov");

		protected override void Update()
		{
			if (Registered)
			{
				base.Update();
				return;
			}

			if (!PreloaderUI.Instantiated)
				return;

			RegisterCommands();
		}

		private Rect _clientWindowRect;
		protected override void OnGUIWhenEnabled()
		{
			_clientWindowRect = new Rect(X, Y, _clientWindowRect.width, _clientWindowRect.height);
			_clientWindowRect = GUILayout.Window(0, _clientWindowRect, RenderFeatureWindow, "EFT Trainer", GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
			X = _clientWindowRect.x;
			Y = _clientWindowRect.y;
		}

		private static void RenderFeatureWindow(int id)
		{
			GUILayout.BeginVertical();
			foreach (var feature in FeatureFactory.GetAllToggleableFeatures().OrderBy(f => f.Name))
			{
				if (feature is Commands or GameState)
					continue;

				var toggleText = " " + GetFeatureHelpText(feature);
				feature.Enabled = GUILayout.Toggle(feature.Enabled, toggleText);
			}
			GUILayout.EndVertical();
			GUI.DragWindow();
		}

		private void RegisterCommands()
		{
			var commands = ConsoleScreen.Commands;
			if (commands.Count == 0)
				return;

			foreach(var feature in FeatureFactory.GetAllToggleableFeatures())
			{
				if (feature is Commands or GameState)
					continue;

				CreateCommand(commands, $"{feature.Name} (?<{ValueGroup}>(on)|(off))", m => OnToggleFeature(feature, m));

				if (feature is not LootItems liFeature) 
					continue;

				CreateCommand(commands, $"list {{0}}( (?<{ValueGroup}>.*))?", m => ListLootItems(m, liFeature));
				CreateCommand(commands, $"listr {{0}}( (?<{ValueGroup}>.*))?", m => ListLootItems(m, liFeature, ELootRarity.Rare));
				CreateCommand(commands, $"listsr {{0}}( (?<{ValueGroup}>.*))?", m => ListLootItems(m, liFeature, ELootRarity.Superrare));

				var colorNames = string.Join("|", ColorConverter.ColorNames());
				CreateCommand(commands, $"track (?<value>.+?)(?<extra> ({colorNames}|\\[[\\.,\\d ]*\\]{{1}}))?", m => TrackLootItem(m, liFeature));
				CreateCommand(commands, $"trackr (?<value>.+?)(?<extra> ({colorNames}|\\[[\\.,\\d ]*\\]{{1}}))?", m => TrackLootItem(m, liFeature, ELootRarity.Rare));
				CreateCommand(commands, $"tracksr (?<value>.+?)(?<extra> ({colorNames}|\\[[\\.,\\d ]*\\]{{1}}))?", m => TrackLootItem(m, liFeature, ELootRarity.Superrare));

				CreateCommand(commands, $"untrack (?<{ValueGroup}>.+)", m => UnTrackLootItem(m, liFeature));
				CreateCommand(commands, "tracklist", _ => ShowTrackList(liFeature));
			}

			CreateCommand(commands, "dump", _ => Dump());
			CreateCommand(commands, "status", _ => Status());

			var configFile = Path.Combine(UserPath, "trainer.ini");
			var features = FeatureFactory.GetAllFeatures();
			CreateCommand(commands, "load", _ => ConfigurationManager.Load(configFile, features));
			CreateCommand(commands, "save", _ => ConfigurationManager.Save(configFile, features));

			// Load default configuration
			ConfigurationManager.Load(configFile, features, false);

			Registered = true;
		}

		private static void CreateCommand(IList commands, string regex, Action<Match> match)
		{
			// 'commands' field is a List<?> where ? is an obfuscated type, distinct for every EFT build
			// so use reflection instead of breaking the build every time
			// and use the non generic IList interface to add a new item
			var listType = commands.GetType();
			var commandType = listType.GetGenericArguments().FirstOrDefault();
			if (commandType == null)
				return;

			var command = Activator.CreateInstance(commandType, regex, match);
			if (command == null)
				return;

			commands.Add(command);
		}

		private static void ShowTrackList(LootItems feature, bool changed = false)
		{
			if (changed)
				AddConsoleLog("Tracking list updated...", "tracker");

			foreach (var item in feature.TrackedNames)
			{
				var extra = item.Rarity.HasValue ? $" ({item.Rarity.Value.Color()})" : string.Empty;
				AddConsoleLog(item.Color.HasValue ? $"Tracking: {item.Name.Color(item.Color.Value)}{extra}" : $"Tracking: {item.Name}", "tracker");
			}
		}

		private static void UnTrackLootItem(Match match, LootItems feature)
		{
			var matchGroup = match.Groups[ValueGroup];
			if (matchGroup is not {Success: true})
				return;

			ShowTrackList(feature, feature.UnTrack(matchGroup.Value));
		}

		private static void TrackLootItem(Match match, LootItems feature, ELootRarity? rarity = null)
		{
			var matchGroup = match.Groups[ValueGroup];
			if (matchGroup is not {Success: true})
				return;

			Color? color = null;
			var extraGroup = match.Groups[ExtraGroup];
			if (extraGroup is {Success: true})
				color = ColorConverter.Parse(extraGroup.Value);

			ShowTrackList(feature, feature.Track(matchGroup.Value, color, rarity));
		}

		private static void ListLootItems(Match match, LootItems feature, ELootRarity? rarityFilter = null)
		{
			var search = string.Empty;
			var matchGroup = match.Groups[ValueGroup];
			if (matchGroup is {Success: true})
				search = matchGroup.Value.Trim();

			var world = Singleton<GameWorld>.Instance;
			if (world == null)
				return;

			var itemsPerName = new Dictionary<string, List<Item>>();

			// Step 1 - look outside containers and inside corpses (loot items)
			FindLootItems(world, itemsPerName, feature);

			// Step 2 - look inside containers (items)
			if (feature.SearchInsideContainers)
				FindItemsInContainers(itemsPerName);

			var names = itemsPerName.Keys.ToList();
			names.Sort();
			names.Reverse();

			var count = 0;
			foreach (var itemName in names)
			{
				if (itemName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
					continue;

				var list = itemsPerName[itemName];
				var rarity = list.First().Template.Rarity;
				if (rarityFilter.HasValue && rarityFilter.Value != rarity)
					continue;

				var extra = rarity != ELootRarity.Not_exist ? $" ({rarity.Color()})" : string.Empty;
				AddConsoleLog($"{itemName} [{list.Count.ToString().Cyan()}]{extra}", "list");

				count += list.Count;
			}

			AddConsoleLog("------", "list");
			AddConsoleLog($"found {count.ToString().Cyan()} items", "list");
		}

		private static void FindItemsInContainers(Dictionary<string, List<Item>> itemsPerName)
		{
			var containers = FindObjectsOfType<LootableContainer>();
			foreach (var container in containers)
			{
				if (!container.IsValid())
					continue;

				FindItemsInRootItem(itemsPerName, container.ItemOwner?.RootItem);
			}
		}

		private static void FindItemsInRootItem(Dictionary<string, List<Item>> itemsPerName, Item? rootItem)
		{
			var items = rootItem?
				.GetAllItems()?
				.ToArray();

			if (items == null)
				return;

			IndexItems(items, itemsPerName);
		}

		private static void FindLootItems(GameWorld world, Dictionary<string, List<Item>> itemsPerName, LootItems feature)
		{
			var lootItems = world.LootItems;
			var filteredItems = new List<Item>();
			for (var i = 0; i < lootItems.Count; i++)
			{
				var lootItem = lootItems.GetByIndex(i);
				if (!lootItem.IsValid())
					continue;

				if (lootItem is Corpse corpse)
				{
					if (feature.SearchInsideCorpses)
						FindItemsInRootItem(itemsPerName, corpse.ItemOwner?.RootItem);

					continue;
				}

				filteredItems.Add(lootItem.Item);
			}

			IndexItems(filteredItems, itemsPerName);
		}

		private static void IndexItems(IEnumerable<Item> items, Dictionary<string, List<Item>> itemsPerName)
		{
			foreach (var item in items)
			{
				if (!item.IsValid())
					continue;

				if (item.IsFiltered())
					continue;

				var itemName = item.ShortName.Localized();
				if (!itemsPerName.TryGetValue(itemName, out var pnList))
				{
					pnList = new List<Item>();
					itemsPerName[itemName] = pnList;
				}

				pnList.Add(item);
			}
		}

		private static string GetFeatureHelpText(ToggleFeature feature)
		{
			var toggleKey = feature.Key != KeyCode.None ? $" ({feature.Key} to toggle)" : string.Empty;
			return $"{feature.Name} is {(feature.Enabled ? "on".Green() : "off".Red())}{toggleKey}";
		}

		private static void Status()
		{
			foreach (var feature in FeatureFactory.GetAllToggleableFeatures().OrderByDescending(f => f.Name))
			{
				if (feature is Commands or GameState)
					continue;

				AddConsoleLog(GetFeatureHelpText(feature), "status");
			}
		}

		private void Dump()
		{
			var dumpfolder = Path.Combine(UserPath, "Dumps");
			var thisDump = Path.Combine(dumpfolder, $"{DateTime.Now:yyyyMMdd-HHmmss}");

			Directory.CreateDirectory(thisDump);

			AddConsoleLog("Dumping scenes...", "dump");
			for (int i = 0; i < SceneManager.sceneCount; i++) 
			{
				var scene = SceneManager.GetSceneAt(i);
				if (!scene.isLoaded)
					continue;

				var json = SceneDumper.DumpScene(scene).ToPrettyJson();
				File.WriteAllText(Path.Combine(thisDump, GetSafeFilename($"@scene - {scene.name}.txt")), json);
			}

			AddConsoleLog("Dumping game objects...", "dump");
			foreach (var go in FindObjectsOfType<GameObject>())
			{
				if (go == null || go.transform.parent != null || !go.activeSelf) 
					continue;

				var filename = GetSafeFilename(go.name + "-" + go.GetHashCode() + ".txt");
				var json = SceneDumper.DumpGameObject(go).ToPrettyJson();
				File.WriteAllText(Path.Combine(thisDump, filename), json);
			}

			AddConsoleLog($"Dump created in {thisDump}", "dump");
		}

		private static string GetSafeFilename(string filename)
		{
			return string.Join("_", filename.Split(Path.GetInvalidFileNameChars()));  	
		}

		public void OnToggleFeature(ToggleFeature feature, Match match)
		{
			var matchGroup = match.Groups[ValueGroup];
			if (matchGroup is not {Success: true})
				return;

			feature.Enabled = matchGroup.Value switch
			{
				"on" => true,
				"off" => false,
				_ => feature.Enabled
			};
		}

		private static void AddConsoleLog(string log, string from = "scheduler")
		{
			if (PreloaderUI.Instantiated)
				PreloaderUI.Instance.Console.AddLog(log, from);
		}
	}
}
