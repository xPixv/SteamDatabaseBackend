﻿/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal static class FullUpdateProcessor
    {
        public static void PerformSync()
        {
            if (Settings.Current.FullRun == FullRunState.NormalUsingMetadata)
            {
                TaskManager.RunAsync(async () =>
                {
                    await FullUpdateAppsMetadata();
                    await FullUpdatePackagesMetadata();
                });

                return;
            }

            List<uint> apps;
            List<uint> packages;

            using (var db = Database.Get())
            {
                if (Settings.Current.FullRun == FullRunState.Enumerate)
                {
                    // TODO: Remove WHERE when normal appids approach 2mil
                    var lastAppID = 50000 + db.ExecuteScalar<int>("SELECT `AppID` FROM `Apps` WHERE `AppID` < 2000000 ORDER BY `AppID` DESC LIMIT 1");
                    var lastSubID = 10000 + db.ExecuteScalar<int>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC LIMIT 1");

                    Log.WriteInfo("Full Run", "Will enumerate {0} apps and {1} packages", lastAppID, lastSubID);

                    // greatest code you've ever seen
                    apps = Enumerable.Range(0, lastAppID).Reverse().Select(i => (uint)i).ToList();
                    packages = Enumerable.Range(0, lastSubID).Reverse().Select(i => (uint)i).ToList();
                }
                else if (Settings.Current.FullRun == FullRunState.TokensOnly)
                {
                    Log.WriteInfo("Full Run", $"Enumerating {PICSTokens.AppTokens.Count} apps and {PICSTokens.PackageTokens.Count} packages that have a token.");

                    apps = PICSTokens.AppTokens.Keys.ToList();
                    packages = PICSTokens.PackageTokens.Keys.ToList();
                }
                else
                {
                    Log.WriteInfo("Full Run", "Doing a full run on all apps and packages in the database.");

                    if (Settings.Current.FullRun == FullRunState.PackagesNormal)
                    {
                        apps = new List<uint>();
                    }
                    else
                    {
                        apps = db.Query<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC").ToList();
                    }

                    packages = db.Query<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC").ToList();
                }
            }

            TaskManager.RunAsync(async () => await RequestUpdateForList(apps, packages));
        }

        private static async Task RequestUpdateForList(List<uint> appIDs, List<uint> packageIDs)
        {
            Log.WriteInfo("Full Run", "Requesting info for {0} apps and {1} packages", appIDs.Count, packageIDs.Count);

            foreach (var list in appIDs.Split(200))
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(list, Enumerable.Empty<uint>()),
                    new PICSTokens.RequestedTokens
                    {
                        Apps = list.ToList()
                    });

                do
                {
                    await Task.Delay(100);
                }
                while (IsBusy());
            }

            if (Settings.Current.FullRun == FullRunState.WithForcedDepots)
            {
                return;
            }

            foreach (var list in packageIDs.Split(1000))
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(Enumerable.Empty<uint>(), list),
                    new PICSTokens.RequestedTokens
                    {
                        Packages = list.ToList()
                    });

                do
                {
                    await Task.Delay(100);
                }
                while (IsBusy());
            }

            LocalConfig.Save();
        }

        public static async Task FullUpdateAppsMetadata()
        {
            Log.WriteInfo(nameof(FullUpdateProcessor), "Doing a full update for apps using metadata requests");

            var db = await Database.GetConnectionAsync();
            var apps = db.Query<uint>("(SELECT `AppID` FROM `Apps` ORDER BY `AppID` DESC) UNION DISTINCT (SELECT `AppID` FROM `SubsApps` WHERE `Type` = 'app') ORDER BY `AppID` DESC").ToList();

            foreach (var list in apps.Split(10000))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(list.Select(PICSTokens.NewAppRequest), Enumerable.Empty<SteamApps.PICSRequest>(), true));

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }
        }

        public static async Task FullUpdatePackagesMetadata()
        {
            Log.WriteInfo(nameof(FullUpdateProcessor), "Doing a full update for packages using metadata requests");

            var db = await Database.GetConnectionAsync();
            var subs = db.Query<uint>("SELECT `SubID` FROM `Subs` ORDER BY `SubID` DESC").ToList();

            foreach (var list in subs.Split(10000))
            {
                JobManager.AddJob(() => Steam.Instance.Apps.PICSGetProductInfo(Enumerable.Empty<SteamApps.PICSRequest>(), list.Select(PICSTokens.NewPackageRequest), true));

                do
                {
                    await Task.Delay(500);
                }
                while (IsBusy());
            }
        }

        public static async Task HandleMetadataInfo(SteamApps.PICSProductInfoCallback callback)
        {
            Log.WriteDebug(nameof(FullUpdateProcessor), $"Received metadata only product info for {callback.Apps.Count} apps and {callback.Packages.Count} packages");
            
            var apps = new List<uint>();
            var subs = new List<uint>();
            var db = await Database.GetConnectionAsync();

            if (callback.Apps.Any())
            {
                var currentChangeNumbers = (await db.QueryAsync<(uint, uint)>(
                    "SELECT `AppID`, `Value` FROM `AppsInfo` WHERE `Key` = @ChangeNumberKey AND `AppID` IN @Apps",
                    new
                    {
                        ChangeNumberKey = KeyNameCache.GetAppKeyID("root_changenumber"),
                        Apps = callback.Apps.Keys
                    }
                )).ToDictionary(x => x.Item1, x => x.Item2);

                foreach (var app in callback.Apps.Values)
                {
                    currentChangeNumbers.TryGetValue(app.ID, out var currentChangeNumber);

                    if (currentChangeNumber != app.ChangeNumber)
                    {
                        Log.WriteInfo(nameof(FullUpdateProcessor), $"App {app.ID} - Change: {currentChangeNumber} -> {app.ChangeNumber}");
                        apps.Add(app.ID);
                    }
                }
            }

            if (callback.Packages.Any())
            {
                var currentChangeNumbers = (await db.QueryAsync<(uint, uint)>(
                    "SELECT `SubID`, `Value` FROM `SubsInfo` WHERE `Key` = @ChangeNumberKey AND `SubID` IN @Subs",
                    new
                    {
                        ChangeNumberKey = KeyNameCache.GetSubKeyID("root_changenumber"),
                        Subs = callback.Packages.Keys
                    }
                )).ToDictionary(x => x.Item1, x => x.Item2);

                foreach (var sub in callback.Packages.Values)
                {
                    currentChangeNumbers.TryGetValue(sub.ID, out var currentChangeNumber);

                    if (currentChangeNumber != sub.ChangeNumber)
                    {
                        Log.WriteInfo(nameof(FullUpdateProcessor), $"Package {sub.ID} - Change: {currentChangeNumber} -> {sub.ChangeNumber}");
                        subs.Add(sub.ID);
                    }
                }
            }

            if (apps.Any() || subs.Any())
            {
                JobManager.AddJob(
                    () => Steam.Instance.Apps.PICSGetAccessTokens(apps, subs),
                    new PICSTokens.RequestedTokens
                    {
                        Apps = apps,
                        Packages = subs,
                    });
            }
        }

        public static bool IsBusy()
        {
            Log.WriteInfo("Full Run", "Jobs: {0} - Tasks: {1} - Processing: {2} - Depot locks: {3}",
                JobManager.JobsCount,
                TaskManager.TasksCount,
                PICSProductInfo.CurrentlyProcessingCount,
                Steam.Instance.DepotProcessor.DepotLocksCount);

            return TaskManager.TasksCount > 0
                   || JobManager.JobsCount > 0
                   || PICSProductInfo.CurrentlyProcessingCount > 50
                   || Steam.Instance.DepotProcessor.DepotLocksCount > 4;
        }
    }
}