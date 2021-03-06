﻿using System;
using CSGO_Demos_Manager.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Threading;
using CSGO_Demos_Manager.Models.Charts;
using CSGO_Demos_Manager.Models.Events;
using CSGO_Demos_Manager.Models.Source;
using CSGO_Demos_Manager.Models.Stats;
using CSGO_Demos_Manager.Properties;
using CSGO_Demos_Manager.Services.Analyzer;
using CSGO_Demos_Manager.Services.Interfaces;
using CSGO_Demos_Manager.Services.Serialization;
using MoreLinq;
using Newtonsoft.Json;

namespace CSGO_Demos_Manager.Services
{
	public class DemosService : IDemosService
	{
		private readonly CacheService _cacheService = new CacheService();

		private readonly ISteamService _steamService;

		public DemosService(ISteamService steamService)
		{
			_steamService = steamService;
		}

		/// <summary>
		/// Check if there are banned players and update their banned flags
		/// </summary>
		/// <param name="demo"></param>
		/// <returns></returns>
		public async Task<Demo> AnalyzeBannedPlayersAsync(Demo demo)
		{
			List<string> ids = demo.Players.Select(playerExtended => playerExtended.SteamId.ToString()).ToList();
			IEnumerable<Suspect> suspects = await _steamService.GetBanStatusForUserList(ids);
			var enumerableSuspects = suspects as IList<Suspect> ?? suspects.ToList();
			if (enumerableSuspects.Any())
			{
				List<string> whitelistIds =  await _cacheService.GetPlayersWhitelist();
				// Update player's flag
				foreach (Suspect suspect in enumerableSuspects)
				{
					PlayerExtended cheater = demo.Players.FirstOrDefault(p => p.SteamId.ToString() == suspect.SteamId);
					if (cheater != null && !whitelistIds.Contains(cheater.SteamId.ToString()))
					{
						if (suspect.GameBanCount > 0)
						{
							demo.HasCheater = true;
							cheater.IsOverwatchBanned = true;
						}
						if (suspect.VacBanned)
						{
							demo.HasCheater = true;
							cheater.IsVacBanned = true;
						}
					}
				}
			}
			return demo;
		}

		private async Task<Demo> GetDemoHeaderAsync(string demoFilePath)
		{
			var demo = await Task.Run(() => DemoAnalyzer.ParseDemoHeader(demoFilePath));
			if (demo == null) return null;

			// If demo is in cache we retrieve its data
			bool demosIsCached = _cacheService.HasDemoInCache(demo);
			if (demosIsCached)
			{
				demo = await _cacheService.GetDemoDataFromCache(demo);
				demo.Source = Source.Factory(demo.SourceName);
				// Update the demo name and path because it may be renamed / moved
				demo.Name = Path.GetFileName(demoFilePath);
				demo.Path = demoFilePath;
			}
			return demo;
		}

		public async Task<List<Demo>> GetDemosHeader(List<string> folders)
		{
			List<Demo> demos = new List<Demo>();

			if (folders.Count > 0)
			{
				foreach (string folder in folders)
				{
					if (Directory.Exists(folder))
					{
						string[] files = Directory.GetFiles(folder, "*.dem");
						foreach (string file in files)
						{
							var demo = await GetDemoHeaderAsync(file);
							if (demo != null && !demos.Contains(demo)) demos.Add(demo);
						}
					}
				}
			}
			return demos;
		}

		public async Task<Demo> AnalyzeDemo(Demo demo, CancellationToken token)
		{
			if (!File.Exists(demo.Path))
			{
				// Demo may be moved to an other folder, just clear cache
				await _cacheService.RemoveDemo(demo);
			}

			DemoAnalyzer analyzer = DemoAnalyzer.Factory(demo);
			
			demo = await analyzer.AnalyzeDemoAsync(token);

			return demo;
		}

		public async Task SaveComment(Demo demo, string comment)
		{
			demo.Comment = comment;
			await _cacheService.WriteDemoDataCache(demo);
		}

		public async Task SaveStatus(Demo demo, string status)
		{
			demo.Status = status;
			await _cacheService.WriteDemoDataCache(demo);
		}

		public async Task SetSource(ObservableCollection<Demo> demos, string source)
		{
			foreach (Demo demo in demos.Where(demo => demo.Type != "POV"))
			{
				switch (source)
				{
					case "valve":
						demo.Source = new Valve();
						break;
					case "esea":
						demo.Source = new Esea();
						break;
					case "ebot":
						demo.Source = new Ebot();
						break;
					case "faceit":
						demo.Source = new Faceit();
						break;
					case "cevo":
						demo.Source = new Cevo();
						break;
					default:
						demo.Source = new Valve();
						break;
				}
				await _cacheService.WriteDemoDataCache(demo);
			}
		}

		public async Task<Demo> AnalyzePlayersPosition(Demo demo, CancellationToken token)
		{
			if (!File.Exists(demo.Path))
			{
				// Demo may be moved to an other folder, just clear cache
				await _cacheService.RemoveDemo(demo);
			}

			DemoAnalyzer analyzer = DemoAnalyzer.Factory(demo);
			analyzer.AnalyzePlayersPosition = true;

			demo = await analyzer.AnalyzeDemoAsync(token);

			await _cacheService.WriteDemoDataCache(demo);

			return demo;
		}

		public async Task<Demo> AnalyzeHeatmapPoints(Demo demo, CancellationToken token)
		{
			if (!File.Exists(demo.Path))
			{
				// Demo may be moved to an other folder, just clear cache
				await _cacheService.RemoveDemo(demo);
			}

			DemoAnalyzer analyzer = DemoAnalyzer.Factory(demo);
			analyzer.AnalyzeHeatmapPoint = true;

			demo = await analyzer.AnalyzeDemoAsync(token);

			return demo;
		}

		/// <summary>
		/// Return demos from JSON backup file
		/// </summary>
		/// <returns></returns>
		public async Task<List<Demo>> GetDemosFromBackup(string jsonFile)
		{
			List<Demo> demos = new List<Demo>();
			if (File.Exists(jsonFile))
			{
				string json = File.ReadAllText(jsonFile);
				demos = await Task.Factory.StartNew(() => JsonConvert.DeserializeObject<List<Demo>>(json, new DemoListBackupConverter()));
			}
			return demos;
		}

		public async Task<Rank> GetLastRankAccountStatsAsync()
		{
			Rank rank = AppSettings.RankList[0];
			List<Demo> demos = await _cacheService.GetDemoListAsync();
			if (demos.Any())
			{
				// demos where account played and only from valve
				List<Demo> demosPlayerList = demos.Where(demo => demo.SourceName == "valve"
				&& demo.Players.FirstOrDefault(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID) != null).ToList();
				if (demosPlayerList.Any())
				{
					Demo lastDemo = demosPlayerList.MaxBy(d => d.Date);
					int rankNumber = lastDemo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).RankNumberNew;
					rank = AppSettings.RankList.First(r => r.Number == rankNumber);
				}
			}

			return rank;
		}

		public async Task<List<RankDateChart>> GetRankDateChartDataAsync()
		{
			List<RankDateChart> datas = new List<RankDateChart>();
			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				List<Demo> demosPlayerList = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID) != null).ToList();
				if (demosPlayerList.Any())
				{
					// Sort by date
					demosPlayerList.Sort((d1, d2) => d1.Date.CompareTo(d2.Date));
					foreach (Demo demo in demosPlayerList)
					{
						// Ignore demos where all players have no rank, sometimes CCSUsrMsg_ServerRankUpdate isn't raised
						if (demo.Players.All(p => p.RankNumberOld != 0))
						{
							int newRankNumber = demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).RankNumberNew;
							int oldRankNumber = demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).RankNumberOld;
							int winCount = demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).WinCount;
							datas.Add(new RankDateChart
							{
								Date = demo.Date,
								OldRank = oldRankNumber,
								NewRank = newRankNumber,
								WinCount = winCount,
								WinStatus = demo.MatchVerdictSelectedAccountCount
							});
						}
					}
				}
			}

			return datas;
		}

		public async Task<OverallStats> GetGeneralAccountStatsAsync()
		{
			OverallStats stats = new OverallStats();

			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				List<Demo> demosPlayerList = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID) != null).ToList();
				if (demosPlayerList.Any())
				{
					stats.MatchCount = demosPlayerList.Count;
					foreach (Demo demo in demosPlayerList)
					{
						stats.KillCount += demo.TotalKillSelectedAccountCount;
						stats.AssistCount += demo.AssistSelectedAccountCount;
						stats.DeathCount += demo.DeathSelectedAccountCount;
						stats.KnifeKillCount += demo.KnifeKillSelectedAccountCount;
						stats.EntryKillCount += demo.EntryKillSelectedAccountCount;
						stats.FiveKillCount += demo.FiveKillSelectedAccountCount;
						stats.FourKillCount += demo.FourKillSelectedAccountCount;
						stats.ThreeKillCount += demo.ThreeKillSelectedAccountCount;
						stats.TwoKillCount += demo.TwoKillSelectedAccountCount;
						stats.HeadshotCount += demo.HeadshotSelectedAccountCount;
						stats.BombDefusedCount += demo.BombDefusedSelectedAccountCount;
						stats.BombExplodedCount += demo.BombExplodedSelectedAccountCount;
						stats.BombPlantedCount += demo.BombPlantedSelectedAccountCount;
						stats.MvpCount += demo.MvpSelectedAccountCount;
						stats.DamageCount += demo.TotalDamageHealthSelectedAccountCount;
						stats.RoundCount += demo.Rounds.Count;
						stats.ClutchCount += demo.ClutchSelectedAccountCount;
						stats.ClutchWin += demo.ClutchWinCountSelectedAccountCount;
						switch (demo.MatchVerdictSelectedAccountCount)
						{
							case -1:
							case -2:
								stats.MatchLossCount++;
								break;
							case 0:
								stats.MatchDrawCount++;
								break;
							case 1:
							case 2:
								stats.MatchWinCount++;
								break;
						}
					}
				}

				if (stats.KillCount != 0 && stats.DeathCount != 0)
				{
					stats.KillDeathRatio = Math.Round(stats.KillCount / (decimal)stats.DeathCount, 2);
				}
				if (stats.KillCount != 0 && stats.HeadshotCount != 0)
				{
					stats.HeadshotRatio = Math.Round(((decimal)stats.HeadshotCount * 100) / stats.KillCount, 2);
				}
			}

			return stats;
		}

		public async Task<MapStats> GetMapStatsAsync()
		{
			MapStats stats = new MapStats();

			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				List<Demo> demosPlayerList = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID) != null).ToList();
				if (demosPlayerList.Any())
				{
					stats.Dust2WinCount = demosPlayerList.Count(d => d.MapName == "de_dust2" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.Dust2LossCount = demosPlayerList.Count(d => d.MapName == "de_dust2" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.Dust2DrawCount = demosPlayerList.Count(d => d.MapName == "de_dust2" && d.MatchVerdictSelectedAccountCount == 0);
					int matchCount = stats.Dust2WinCount + stats.Dust2LossCount + stats.Dust2DrawCount;
					if (matchCount > 0)
					{
						stats.Dust2WinPercentage = Math.Round((stats.Dust2WinCount / (double)matchCount * 100), 2);
					}

					stats.MirageWinCount = demosPlayerList.Count(d => d.MapName == "de_mirage" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.MirageLossCount = demosPlayerList.Count(d => d.MapName == "de_mirage" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.MirageDrawCount = demosPlayerList.Count(d => d.MapName == "de_mirage" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.MirageWinCount + stats.MirageLossCount + stats.MirageDrawCount;
					if (matchCount > 0)
					{
						stats.MirageWinPercentage = Math.Round((stats.MirageWinCount / (double)matchCount * 100), 2);
					}

					stats.InfernoWinCount = demosPlayerList.Count(d => d.MapName == "de_inferno" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.InfernoLossCount = demosPlayerList.Count(d => d.MapName == "de_inferno" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.InfernoDrawCount = demosPlayerList.Count(d => d.MapName == "de_inferno" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.InfernoWinCount + stats.InfernoLossCount + stats.InfernoDrawCount;
					if (matchCount > 0)
					{
						stats.InfernoWinPercentage = Math.Round((stats.InfernoWinCount / (double)matchCount * 100), 2);
					}

					stats.TrainWinCount = demosPlayerList.Count(d => d.MapName == "de_train" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.TrainLossCount = demosPlayerList.Count(d => d.MapName == "de_train" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.TrainDrawCount = demosPlayerList.Count(d => d.MapName == "de_train" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.TrainWinCount + stats.TrainLossCount + stats.TrainDrawCount;
					if (matchCount > 0)
					{
						stats.TrainWinPercentage = Math.Round((stats.TrainWinCount / (double)matchCount * 100), 2);
					}

					stats.OverpassWinCount = demosPlayerList.Count(d => d.MapName == "de_overpass" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.OverpassLossCount = demosPlayerList.Count(d => d.MapName == "de_overpass" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.OverpassDrawCount = demosPlayerList.Count(d => d.MapName == "de_overpass" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.OverpassWinCount + stats.OverpassLossCount + stats.OverpassDrawCount;
					if (matchCount > 0)
					{
						stats.OverpassWinPercentage = Math.Round((stats.OverpassWinCount / (double)matchCount * 100), 2);
					}

					stats.CacheWinCount = demosPlayerList.Count(d => d.MapName == "de_cache" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.CacheLossCount = demosPlayerList.Count(d => d.MapName == "de_cache" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.CacheDrawCount = demosPlayerList.Count(d => d.MapName == "de_cache" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.CacheWinCount + stats.CacheLossCount + stats.CacheDrawCount;
					if (matchCount > 0)
					{
						stats.CacheWinPercentage = Math.Round((stats.CacheWinCount / (double)matchCount * 100), 2);
					}

					stats.CobblestoneWinCount = demosPlayerList.Count(d => d.MapName == "de_cbble" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.CobblestoneLossCount = demosPlayerList.Count(d => d.MapName == "de_cbble" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.CobblestoneDrawCount = demosPlayerList.Count(d => d.MapName == "de_cbble" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.CobblestoneWinCount + stats.CobblestoneLossCount + stats.CobblestoneDrawCount;
					if (matchCount > 0)
					{
						stats.CobblestoneWinPercentage = Math.Round((stats.CobblestoneWinCount / (double)matchCount * 100), 2);
					}

					stats.NukeWinCount = demosPlayerList.Count(d => d.MapName == "de_nuke" && (d.MatchVerdictSelectedAccountCount == 1 || d.MatchVerdictSelectedAccountCount == 2));
					stats.NukeLossCount = demosPlayerList.Count(d => d.MapName == "de_nuke" && (d.MatchVerdictSelectedAccountCount == -1 || d.MatchVerdictSelectedAccountCount == -2));
					stats.NukeDrawCount = demosPlayerList.Count(d => d.MapName == "de_nuke" && d.MatchVerdictSelectedAccountCount == 0);
					matchCount = stats.NukeWinCount + stats.NukeLossCount + stats.NukeDrawCount;
					if (matchCount > 0)
					{
						stats.NukeWinPercentage = Math.Round((stats.NukeWinCount / (double)matchCount * 100), 2);
					}
				}
			}

			return stats;
		}

		public async Task<WeaponStats> GetWeaponStatsAsync()
		{
			WeaponStats stats = new WeaponStats();

			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				List<Demo> demosPlayerList = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID) != null).ToList();
				if (demosPlayerList.Any())
				{
					foreach (Demo demo in demosPlayerList)
					{
						// Rifles
						stats.KillAk47Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "AK-47");
						stats.DeathAk47Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "AK-47");

						stats.KillM4A4Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "M4A4");
						stats.DeathM4A4Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "M4A4");

						stats.KillM4A1Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "M4A1");
						stats.DeathM4A1Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "M4A1");

						stats.KillAugCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "AUG");
						stats.DeathAugCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "AUG");

						stats.KillGalilarCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Galil AR");
						stats.DeathGalilarCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Galil AR");

						stats.KillSg553Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "SG 553");
						stats.DeathSg553Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "SG 553");

						stats.KillFamasCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Famas");
						stats.DeathFamasCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Famas");

						// Snipers
						stats.KillAwpCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "AWP");
						stats.DeathAwpCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "AWP");

						stats.KillScoutCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "SSG 08 (Scout)");
						stats.DeathScoutCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "SSG 08 (Scout)");

						stats.KillScar20Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Scar-20 (Autonoob)");
						stats.DeathScar20Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Scar-20 (Autonoob)");

						stats.KillG3Sg1Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "G3SG1 (Autonoob)");
						stats.DeathG3Ssg1Count+=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "G3SG1 (Autonoob)");

						// SMGs
						stats.KillMp7Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MP7");
						stats.DeathMp7Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MP7");

						stats.KillMp9Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MP9");
						stats.DeathMp9Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MP9");

						stats.KillP90Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "P90");
						stats.DeathP90Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "P90");

						stats.KillBizonCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "PP-Bizon");
						stats.DeathBizonCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "PP-Bizon");

						stats.KillMac10Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MAC-10");
						stats.DeathMac10Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MAC-10");

						stats.KillUmp45Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "UMP-45");
						stats.DeathUmp45Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "UMP-45");

						// Heavy
						stats.KillNovaCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Nova");
						stats.DeathNovaCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Nova");

						stats.KillXm1014Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "XM1014");
						stats.DeathXm1014Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "XM1014");

						stats.KillMag7Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MAG-7");
						stats.DeathMag7Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "MAG-7");

						stats.KillSawedOffCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Sawed-Off");
						stats.DeathSawedOffCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Sawed-Off");

						stats.KillM249Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "M249");
						stats.DeathM249Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "M249");

						stats.KillNegevCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Negev");
						stats.DeathNegevCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Negev");

						// Pistols
						stats.KillGlockCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Glock-18");
						stats.DeathGlockCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Glock-18");

						stats.KillUspCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "USP-S");
						stats.DeathUspCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "USP-S");

						stats.KillP2000Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "P2000");
						stats.DeathP2000Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "P2000");

						stats.KillP250Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "P250");
						stats.DeathP250Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "P250");

						stats.KillCz75Count +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "CZ75-Auto");
						stats.DeathCz75Count +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "CZ75-Auto");

						stats.KillDeagleCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Deagle");
						stats.DeathDeagleCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Deagle");

						stats.KillDualEliteCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Dual Barettas");
						stats.DeathDualEliteCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Dual Barettas");

						stats.KillFiveSevenCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Five-SeveN");
						stats.DeathFiveSevenCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Five-SeveN");

						// Equipment
						stats.KillHeGrenadeCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "He grenade");
						stats.DeathHeGrenadeCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "He grenade");

						stats.KillMolotovCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Molotov");
						stats.DeathMolotovCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Molotov");

						stats.KillIncendiaryCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Incendiary");
						stats.DeathIncendiaryCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Incendiary");

						stats.KillTazerCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Zeus (Tazer)");
						stats.DeathTazerCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Zeus (Tazer)");

						stats.KillKnifeCount +=
							demo.Kills.Count(
								k => k.KillerSteamId != 0 && k.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Knife");
						stats.DeathKnifeCount +=
							demo.Kills.Count(
								k => k.KilledSteamId != 0 && k.KilledSteamId == Settings.Default.SelectedStatsAccountSteamID && k.Weapon.Name == "Knife");

						stats.FlashbangThrownCount +=
							demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).FlashbangThrownCount;
						stats.SmokeThrownCount +=
							demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).SmokeThrownCount;
						stats.DecoyThrownCount +=
							demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).DecoyThrownCount;
						stats.HeGrenadeThrownCount +=
							demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).HeGrenadeThrownCount;
						stats.MolotovThrownCount +=
							demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).MolotovThrownCount;
						stats.IncendiaryThrownCount +=
							demo.Players.First(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID).IncendiaryThrownCount;
					}
				}
			}

			return stats;
		}

		public async Task<List<Demo>> GetDemosPlayer(string steamId)
		{
			List<Demo> result = new List<Demo>();

			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				result = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId.ToString() == steamId) != null).ToList();
				for(int i = 0; i < result.Count; i++)
				{
					result[i] = await GetDemoHeaderAsync(result[i].Path);
				}
			}

			return result;
		}

		public async Task<ProgressStats> GetProgressStatsAsync()
		{
			ProgressStats stats = new ProgressStats();
			List<Demo> demos = await _cacheService.GetDemoListAsync();

			if (demos.Any())
			{
				List<Demo> demosPlayerList = demos.Where(demo => demo.Players.FirstOrDefault(p => p.SteamId == Settings.Default.SelectedStatsAccountSteamID) != null).ToList();
				if (demosPlayerList.Any())
				{
					double maximumVelocity = 0;
					demosPlayerList.Sort((d1, d2) => d1.Date.CompareTo(d2.Date));
					DateTime currentDate = new DateTime(demosPlayerList[0].Date.Year, demosPlayerList[0].Date.Month, demosPlayerList[0].Date.Day);
					int matchCount = 0;
					int winCount = 0;
					int headshotCount = 0;
					int killCount = 0;
					int deathCount = 0;
					int damageCount = 0;
					int rifleKillCount = 0;
					int heavyKillCount = 0;
					int sniperKillCount = 0;
					int pistolKillCount = 0;
					int smgKillCount = 0;
					int crouchKillCount = 0;
					Dictionary<WeaponType, double> velocityStats = new Dictionary<WeaponType, double>();
					foreach (Demo demo in demosPlayerList)
					{
						matchCount++;
						DateTime demoDate = new DateTime(demo.Date.Year, demo.Date.Month, demo.Date.Day);
						if (demo.Equals(demosPlayerList.First()) || demo.Equals(demosPlayerList.Last())
							|| demoDate >= currentDate.AddDays(7))
						{
							stats.Win.Add(new WinDateChart
							{
								Date = demoDate,
								WinPercentage = 0
							});
							stats.HeadshotRatio.Add(new HeadshotDateChart
							{
								Date = demoDate,
								HeadshotPercentage = 0
							});
							stats.Damage.Add(new DamageDateChart
							{
								Date = demoDate,
								DamageCount = 0
							});
							stats.Kill.Add(new KillDateChart
							{
								Date = demoDate,
								KillAverage = 0,
								DeathAverage = 0
							});
							stats.KillVelocityRifle.Add(new KillVelocityChart
							{
								Date = demoDate,
								VelocityAverage = 0
							});
							stats.KillVelocityPistol.Add(new KillVelocityChart
							{
								Date = demoDate,
								VelocityAverage = 0
							});
							stats.KillVelocitySmg.Add(new KillVelocityChart
							{
								Date = demoDate,
								VelocityAverage = 0
							});
							stats.KillVelocitySniper.Add(new KillVelocityChart
							{
								Date = demoDate,
								VelocityAverage = 0
							});
							stats.KillVelocityHeavy.Add(new KillVelocityChart
							{
								Date = demoDate,
								VelocityAverage = 0
							});
							stats.CrouchKill.Add(new CrouchKillDateChart
							{
								Date = demoDate,
								CrouchKillPercentage = 0
							});

							currentDate = demoDate;
							matchCount = 1;
							winCount = 0;
							headshotCount = 0;
							killCount = 0;
							deathCount = 0;
							damageCount = 0;
							rifleKillCount = 0;
							heavyKillCount = 0;
							sniperKillCount = 0;
							pistolKillCount = 0;
							smgKillCount = 0;
							crouchKillCount = 0;
							velocityStats.Clear();
						}

						if (demo.MatchVerdictSelectedAccountCount == 1 || demo.MatchVerdictSelectedAccountCount == 2) winCount++;
						if (winCount > 0) stats.Win.Last().WinPercentage = Math.Round((winCount / (double)matchCount * 100), 2);
						headshotCount += demo.HeadshotSelectedAccountCount;
						killCount += demo.TotalKillSelectedAccountCount;
						deathCount += demo.DeathSelectedAccountCount;
						damageCount += demo.TotalDamageHealthSelectedAccountCount + demo.TotalDamageArmorSelectedAccountCount;
						crouchKillCount += demo.CrouchKillSelectedAccountCount;

						if (killCount > 0)
						{
							stats.HeadshotRatio.Last().HeadshotPercentage = Math.Round((headshotCount / (double)killCount * 100), 2);
							stats.CrouchKill.Last().CrouchKillPercentage = Math.Round((crouchKillCount / (double)killCount * 100), 2);
						}
						stats.Damage.Last().DamageCount = Math.Round((double)damageCount/ matchCount, 2);
						stats.Kill.Last().KillAverage = Math.Round((double)killCount/ matchCount, 1);
						stats.Kill.Last().DeathAverage = Math.Round((double)deathCount / matchCount, 1);

						foreach (KillEvent e in demo.Kills)
						{
							if (e.KillerSteamId == Settings.Default.SelectedStatsAccountSteamID)
							{
								if(!velocityStats.ContainsKey(e.Weapon.Type)) velocityStats.Add(e.Weapon.Type, 0);
								switch (e.Weapon.Type)
								{
									case WeaponType.Rifle:
										rifleKillCount++;
										break;
									case WeaponType.Heavy:
										heavyKillCount++;
										break;
									case WeaponType.Pistol:
										pistolKillCount++;
										break;
									case  WeaponType.SMG:
										smgKillCount++;
										break;
									case WeaponType.Sniper:
										sniperKillCount++;
										break;
								}
								velocityStats[e.Weapon.Type] += Math.Abs(e.KillerVelocityY + e.KillerVelocityX + e.KillerVelocityZ);
							}
						}

						if (velocityStats.ContainsKey(WeaponType.Rifle))
							stats.KillVelocityRifle.Last().VelocityAverage = Math.Round(velocityStats[WeaponType.Rifle] / rifleKillCount, 1);
						if (velocityStats.ContainsKey(WeaponType.Pistol))
							stats.KillVelocityPistol.Last().VelocityAverage = Math.Round(velocityStats[WeaponType.Pistol] / pistolKillCount, 1);
						if (velocityStats.ContainsKey(WeaponType.Sniper))
							stats.KillVelocitySniper.Last().VelocityAverage = Math.Round(velocityStats[WeaponType.Sniper] / sniperKillCount, 1);
						if (velocityStats.ContainsKey(WeaponType.SMG))
							stats.KillVelocitySmg.Last().VelocityAverage = Math.Round(velocityStats[WeaponType.SMG] / smgKillCount, 1);
						if (velocityStats.ContainsKey(WeaponType.Heavy))
							stats.KillVelocityHeavy.Last().VelocityAverage = Math.Round(velocityStats[WeaponType.Heavy] / heavyKillCount, 1);
					}
					maximumVelocity = stats.KillVelocityPistol.Select(k
						=> k.VelocityAverage).Concat(new[] {maximumVelocity}).Max();
					maximumVelocity = stats.KillVelocityRifle.Select(k
						=> k.VelocityAverage).Concat(new[] { maximumVelocity }).Max();
					maximumVelocity = stats.KillVelocityHeavy.Select(k
						=> k.VelocityAverage).Concat(new[] { maximumVelocity }).Max();
					maximumVelocity = stats.KillVelocitySmg.Select(k
						=> k.VelocityAverage).Concat(new[] { maximumVelocity }).Max();
					maximumVelocity = stats.KillVelocitySniper.Select(k
						=> k.VelocityAverage).Concat(new[] { maximumVelocity }).Max();
					stats.MaximumVelocity = maximumVelocity;
				}
			}

			return stats;
		}

		public async Task<bool> DeleteDemo(Demo demo)
		{
			if (!File.Exists(demo.Path)) return false;
			await _cacheService.RemoveDemo(demo);
			if (File.Exists(demo.Path + ".info")) File.Delete(demo.Path + ".info");
			File.Delete(demo.Path);

			return true;
		}
	}
}
