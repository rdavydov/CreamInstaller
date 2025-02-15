﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using CreamInstaller.Components;
using CreamInstaller.Epic;
using CreamInstaller.Paradox;
using CreamInstaller.Steam;
using CreamInstaller.Utility;

using Gameloop.Vdf.Linq;

namespace CreamInstaller;

internal partial class SelectForm : CustomForm
{
    internal SelectForm(IWin32Window owner) : base(owner)
    {
        InitializeComponent();
        Text = Program.ApplicationName;
    }

    private static void UpdateRemaining(Label label, List<string> list, string descriptor) =>
        label.Text = list.Any() ? $"Remaining {descriptor} ({list.Count}): " + string.Join(", ", list).Replace("&", "&&") : "";

    private readonly List<string> RemainingGames = new();
    private void UpdateRemainingGames() => UpdateRemaining(progressLabelGames, RemainingGames, "games");
    private void AddToRemainingGames(string gameName)
    {
        if (Program.Canceled) return;
        Program.Invoke(progressLabelGames, delegate
        {
            if (Program.Canceled) return;
            if (!RemainingGames.Contains(gameName))
                RemainingGames.Add(gameName);
            UpdateRemainingGames();
        });
    }
    private void RemoveFromRemainingGames(string gameName)
    {
        if (Program.Canceled) return;
        Program.Invoke(progressLabelGames, delegate
        {
            if (Program.Canceled) return;
            if (RemainingGames.Contains(gameName))
                RemainingGames.Remove(gameName);
            UpdateRemainingGames();
        });
    }

    private readonly List<string> RemainingDLCs = new();
    private void UpdateRemainingDLCs() => UpdateRemaining(progressLabelDLCs, RemainingDLCs, "DLCs");
    private void AddToRemainingDLCs(string dlcId)
    {
        if (Program.Canceled) return;
        Program.Invoke(progressLabelDLCs, delegate
        {
            if (Program.Canceled) return;
            if (!RemainingDLCs.Contains(dlcId))
                RemainingDLCs.Add(dlcId);
            UpdateRemainingDLCs();
        });
    }
    private void RemoveFromRemainingDLCs(string dlcId)
    {
        if (Program.Canceled) return;
        Program.Invoke(progressLabelDLCs, delegate
        {
            if (Program.Canceled) return;
            if (RemainingDLCs.Contains(dlcId))
                RemainingDLCs.Remove(dlcId);
            UpdateRemainingDLCs();
        });
    }

    private async Task GetApplicablePrograms(IProgress<int> progress)
    {
        if (ProgramsToScan is null || !ProgramsToScan.Any()) return;
        int TotalGameCount = 0;
        int CompleteGameCount = 0;
        void AddToRemainingGames(string gameName)
        {
            this.AddToRemainingGames(gameName);
            progress.Report(-++TotalGameCount);
            progress.Report(CompleteGameCount);
        }
        void RemoveFromRemainingGames(string gameName)
        {
            this.RemoveFromRemainingGames(gameName);
            progress.Report(++CompleteGameCount);
        }
        if (Program.Canceled) return;
        List<TreeNode> treeNodes = TreeNodes;
        RemainingGames.Clear(); // for display purposes only, otherwise ignorable
        RemainingDLCs.Clear(); // for display purposes only, otherwise ignorable
        List<Task> appTasks = new();
        if (Directory.Exists(ParadoxLauncher.InstallPath) && ProgramsToScan.Any(c => c.platform == "Paradox" && c.id == "ParadoxLauncher"))
        {
            List<string> steamDllDirectories = await SteamLibrary.GetDllDirectoriesFromGameDirectory(ParadoxLauncher.InstallPath);
            List<string> epicDllDirectories = await EpicLibrary.GetDllDirectoriesFromGameDirectory(ParadoxLauncher.InstallPath);
            if (steamDllDirectories is not null || epicDllDirectories is not null)
            {
                ProgramSelection selection = ProgramSelection.FromId("ParadoxLauncher");
                selection ??= new();
                if (allCheckBox.Checked) selection.Enabled = true;
                selection.Id = "ParadoxLauncher";
                selection.Name = "Paradox Launcher";
                selection.RootDirectory = ParadoxLauncher.InstallPath;
                selection.DllDirectories = steamDllDirectories ?? epicDllDirectories;
                selection.IsSteam = steamDllDirectories is not null;
                selection.IsEpic = epicDllDirectories is not null;

                TreeNode programNode = treeNodes.Find(s => s.Name == selection.Id) ?? new();
                programNode.Name = selection.Id;
                programNode.Text = selection.Name;
                programNode.Checked = selection.Enabled;
                programNode.Remove();
                selectionTreeView.Nodes.Add(programNode);
            }
        }
        if (Directory.Exists(SteamLibrary.InstallPath) && ProgramsToScan.Any(c => c.platform == "Steam"))
        {
            List<Tuple<string, string, string, int, string>> steamGames = await SteamLibrary.GetGames();
            foreach (Tuple<string, string, string, int, string> program in steamGames)
            {
                string appId = program.Item1;
                string name = program.Item2;
                string branch = program.Item3;
                int buildId = program.Item4;
                string directory = program.Item5;
                if (Program.Canceled) return;
                Thread.Sleep(0);
                if (Program.IsGameBlocked(name, directory) || !ProgramsToScan.Any(c => c.id == appId)) continue;
                AddToRemainingGames(name);
                Task task = Task.Run(async () =>
                {
                    if (Program.Canceled) return;
                    Thread.Sleep(0);
                    List<string> dllDirectories = await SteamLibrary.GetDllDirectoriesFromGameDirectory(directory);
                    if (dllDirectories is null)
                    {
                        RemoveFromRemainingGames(name);
                        return;
                    }
                    AppData appData = await SteamStore.QueryStoreAPI(appId);
                    VProperty appInfo = await SteamCMD.GetAppInfo(appId, branch, buildId);
                    if (appData is null && appInfo is null)
                    {
                        RemoveFromRemainingGames(name);
                        return;
                    }
                    if (Program.Canceled) return;
                    ConcurrentDictionary<string, (DlcType type, string name, string icon)> dlc = new();
                    List<Task> dlcTasks = new();
                    List<string> dlcIds = new();
                    if (appData is not null) dlcIds.AddRange(await SteamStore.ParseDlcAppIds(appData));
                    if (appInfo is not null) dlcIds.AddRange(await SteamCMD.ParseDlcAppIds(appInfo));
                    if (dlcIds.Count > 0)
                    {
                        foreach (string dlcAppId in dlcIds)
                        {
                            if (Program.Canceled) return;
                            Thread.Sleep(0);
                            AddToRemainingDLCs(dlcAppId);
                            Task task = Task.Run(async () =>
                            {
                                if (Program.Canceled) return;
                                Thread.Sleep(0);
                                string dlcName = null;
                                string dlcIcon = null;
                                AppData dlcAppData = await SteamStore.QueryStoreAPI(dlcAppId, true);
                                if (dlcAppData is not null)
                                {
                                    dlcName = dlcAppData.name;
                                    dlcIcon = dlcAppData.header_image;
                                }
                                else
                                {
                                    VProperty dlcAppInfo = await SteamCMD.GetAppInfo(dlcAppId);
                                    if (dlcAppInfo is not null)
                                    {
                                        dlcName = dlcAppInfo.Value?.GetChild("common")?.GetChild("name")?.ToString();
                                        string dlcIconStaticId = dlcAppInfo.Value?.GetChild("common")?.GetChild("icon")?.ToString();
                                        dlcIconStaticId ??= dlcAppInfo.Value?.GetChild("common")?.GetChild("logo_small")?.ToString();
                                        dlcIconStaticId ??= dlcAppInfo.Value?.GetChild("common")?.GetChild("logo")?.ToString();
                                        if (dlcIconStaticId is not null)
                                            dlcIcon = IconGrabber.SteamAppImagesPath + @$"\{dlcAppId}\{dlcIconStaticId}.jpg";
                                    }
                                }
                                if (Program.Canceled) return;
                                if (!string.IsNullOrWhiteSpace(dlcName))
                                    dlc[dlcAppId] = (DlcType.Steam, dlcName, dlcIcon);
                                RemoveFromRemainingDLCs(dlcAppId);
                            });
                            dlcTasks.Add(task);
                            Thread.Sleep(10); // to reduce control & window freezing
                        }
                    }
                    else
                    {
                        RemoveFromRemainingGames(name);
                        return;
                    }
                    if (Program.Canceled) return;
                    foreach (Task task in dlcTasks)
                    {
                        if (Program.Canceled) return;
                        await task;
                    }

                    ProgramSelection selection = ProgramSelection.FromId(appId) ?? new();
                    selection.Enabled = allCheckBox.Checked || selection.SelectedDlc.Any() || selection.ExtraDlc.Any();
                    selection.Id = appId;
                    selection.Name = appData?.name ?? name;
                    selection.RootDirectory = directory;
                    selection.DllDirectories = dllDirectories;
                    selection.IsSteam = true;
                    selection.ProductUrl = "https://store.steampowered.com/app/" + appId;
                    selection.IconUrl = IconGrabber.SteamAppImagesPath + @$"\{appId}\{appInfo?.Value?.GetChild("common")?.GetChild("icon")?.ToString()}.jpg";
                    selection.SubIconUrl = appData?.header_image ?? IconGrabber.SteamAppImagesPath + @$"\{appId}\{appInfo?.Value?.GetChild("common")?.GetChild("clienticon")?.ToString()}.ico";
                    selection.Publisher = appData?.publishers[0] ?? appInfo?.Value?.GetChild("extended")?.GetChild("publisher")?.ToString();

                    if (Program.Canceled) return;
                    Program.Invoke(selectionTreeView, delegate
                    {
                        if (Program.Canceled) return;
                        Thread.Sleep(0);
                        TreeNode programNode = treeNodes.Find(s => s.Name == appId) ?? new();
                        programNode.Name = appId;
                        programNode.Text = appData?.name ?? name;
                        programNode.Checked = selection.Enabled;
                        programNode.Remove();
                        selectionTreeView.Nodes.Add(programNode);
                        foreach (KeyValuePair<string, (DlcType type, string name, string icon)> pair in dlc)
                        {
                            if (Program.Canceled || programNode is null) return;
                            Thread.Sleep(0);
                            string appId = pair.Key;
                            (DlcType type, string name, string icon) dlcApp = pair.Value;
                            selection.AllDlc[appId] = dlcApp;
                            if (allCheckBox.Checked) selection.SelectedDlc[appId] = dlcApp;
                            TreeNode dlcNode = treeNodes.Find(s => s.Name == appId) ?? new();
                            dlcNode.Name = appId;
                            dlcNode.Text = dlcApp.name;
                            dlcNode.Checked = selection.SelectedDlc.ContainsKey(appId);
                            dlcNode.Remove();
                            programNode.Nodes.Add(dlcNode);
                        }
                    });
                    if (Program.Canceled) return;
                    RemoveFromRemainingGames(name);
                });
                appTasks.Add(task);
            }
        }
        if (Directory.Exists(EpicLibrary.EpicManifestsPath) && ProgramsToScan.Any(c => c.platform == "Epic"))
        {
            List<Manifest> epicGames = await EpicLibrary.GetGames();
            foreach (Manifest manifest in epicGames)
            {
                string @namespace = manifest.CatalogNamespace;
                string name = manifest.DisplayName;
                string directory = manifest.InstallLocation;
                if (Program.Canceled) return;
                Thread.Sleep(0);
                if (Program.IsGameBlocked(name, directory) || !ProgramsToScan.Any(c => c.id == @namespace)) continue;
                AddToRemainingGames(name);
                Task task = Task.Run(async () =>
                {
                    if (Program.Canceled) return;
                    Thread.Sleep(0);
                    List<string> dllDirectories = await EpicLibrary.GetDllDirectoriesFromGameDirectory(directory);
                    if (dllDirectories is null)
                    {
                        RemoveFromRemainingGames(name);
                        return;
                    }
                    if (Program.Canceled) return;
                    ConcurrentDictionary<string, (string name, string product, string icon, string developer)> entitlements = new();
                    List<Task> dlcTasks = new();
                    List<(string id, string name, string product, string icon, string developer)> entitlementIds = await EpicStore.QueryEntitlements(@namespace);
                    if (entitlementIds.Any())
                    {
                        foreach ((string id, string name, string product, string icon, string developer) in entitlementIds)
                        {
                            if (Program.Canceled) return;
                            Thread.Sleep(0);
                            AddToRemainingDLCs(id);
                            Task task = Task.Run(() =>
                            {
                                if (Program.Canceled) return;
                                Thread.Sleep(0);
                                entitlements[id] = (name, product, icon, developer);
                                RemoveFromRemainingDLCs(id);
                            });
                            dlcTasks.Add(task);
                            Thread.Sleep(10); // to reduce control & window freezing
                        }
                    }
                    if (/*!catalogItems.Any() && */!entitlements.Any())
                    {
                        RemoveFromRemainingGames(name);
                        return;
                    }
                    if (Program.Canceled) return;
                    foreach (Task task in dlcTasks)
                    {
                        if (Program.Canceled) return;
                        await task;
                    }

                    ProgramSelection selection = ProgramSelection.FromId(@namespace) ?? new();
                    selection.Enabled = allCheckBox.Checked || selection.SelectedDlc.Any() || selection.ExtraDlc.Any();
                    selection.Id = @namespace;
                    selection.Name = name;
                    selection.RootDirectory = directory;
                    selection.DllDirectories = dllDirectories;
                    selection.IsEpic = true;
                    foreach (KeyValuePair<string, (string name, string product, string icon, string developer)> pair in entitlements)
                    {
                        Thread.Sleep(0);
                        if (pair.Value.name == selection.Name)
                        {
                            selection.ProductUrl = "https://www.epicgames.com/store/product/" + pair.Value.product;
                            selection.IconUrl = pair.Value.icon;
                            selection.Publisher = pair.Value.developer;
                        }
                    }

                    if (Program.Canceled) return;
                    Program.Invoke(selectionTreeView, delegate
                    {
                        if (Program.Canceled) return;
                        Thread.Sleep(0);
                        TreeNode programNode = treeNodes.Find(s => s.Name == @namespace) ?? new();
                        programNode.Name = @namespace;
                        programNode.Text = name;
                        programNode.Checked = selection.Enabled;
                        programNode.Remove();
                        selectionTreeView.Nodes.Add(programNode);
                        /*TreeNode catalogItemsNode = treeNodes.Find(s => s.Name == @namespace + "_catalogItems") ?? new();
                        catalogItemsNode.Name = @namespace + "_catalogItems";
                        catalogItemsNode.Text = "Catalog Items";
                        catalogItemsNode.Checked = selection.SelectedDlc.Any(pair => pair.Value.type == DlcType.CatalogItem);
                        catalogItemsNode.Remove();
                        programNode.Nodes.Add(catalogItemsNode);*/
                        if (entitlements.Any())
                        {
                            /*TreeNode entitlementsNode = treeNodes.Find(s => s.Name == @namespace + "_entitlements") ?? new();
                            entitlementsNode.Name = @namespace + "_entitlements";
                            entitlementsNode.Text = "Entitlements";
                            entitlementsNode.Checked = selection.SelectedDlc.Any(pair => pair.Value.type == DlcType.Entitlement);
                            entitlementsNode.Remove();
                            programNode.Nodes.Add(entitlementsNode);*/
                            foreach (KeyValuePair<string, (string name, string product, string icon, string developer)> pair in entitlements)
                            {
                                if (programNode is null/* || entitlementsNode is null*/) return;
                                Thread.Sleep(0);
                                string dlcId = pair.Key;
                                (DlcType type, string name, string icon) dlcApp = (DlcType.EpicEntitlement, pair.Value.name, pair.Value.icon);
                                selection.AllDlc[dlcId] = dlcApp;
                                if (allCheckBox.Checked) selection.SelectedDlc[dlcId] = dlcApp;
                                TreeNode dlcNode = treeNodes.Find(s => s.Name == dlcId) ?? new();
                                dlcNode.Name = dlcId;
                                dlcNode.Text = dlcApp.name;
                                dlcNode.Checked = selection.SelectedDlc.ContainsKey(dlcId);
                                dlcNode.Remove();
                                programNode.Nodes.Add(dlcNode); //entitlementsNode.Nodes.Add(dlcNode);
                            }
                        }
                    });
                    if (Program.Canceled) return;
                    RemoveFromRemainingGames(name);
                });
                appTasks.Add(task);
            }
        }
        foreach (Task task in appTasks)
        {
            if (Program.Canceled) return;
            await task;
        }
    }

    private List<(string platform, string id, string name)> ProgramsToScan;
    private async void OnLoad(bool forceScan = false, bool forceProvideChoices = false)
    {
        Program.Canceled = false;
        blockedGamesCheckBox.Enabled = false;
        blockProtectedHelpButton.Enabled = false;
        cancelButton.Enabled = true;
        scanButton.Enabled = false;
        noneFoundLabel.Visible = false;
        allCheckBox.Enabled = false;
        installButton.Enabled = false;
        uninstallButton.Enabled = installButton.Enabled;
        selectionTreeView.Enabled = false;
        progressLabel.Text = "Waiting for user to select which programs/games to scan . . .";
        ShowProgressBar();

        await ProgramData.Setup();

        bool scan = forceScan;
        if (!scan && (ProgramsToScan is null || !ProgramsToScan.Any() || forceProvideChoices))
        {
            List<(string platform, string id, string name, bool alreadySelected)> gameChoices = new();
            if (Directory.Exists(ParadoxLauncher.InstallPath))
                gameChoices.Add(("Paradox", "ParadoxLauncher", "Paradox Launcher", ProgramsToScan is not null && ProgramsToScan.Any(p => p.id == "ParadoxLauncher")));
            if (Directory.Exists(SteamLibrary.InstallPath))
                foreach (Tuple<string, string, string, int, string> program in await SteamLibrary.GetGames())
                    if (!Program.IsGameBlocked(program.Item2, program.Item5))
                        gameChoices.Add(("Steam", program.Item1, program.Item2, ProgramsToScan is not null && ProgramsToScan.Any(p => p.id == program.Item1)));
            if (Directory.Exists(EpicLibrary.EpicManifestsPath))
                foreach (Manifest manifest in await EpicLibrary.GetGames())
                    if (!Program.IsGameBlocked(manifest.DisplayName, manifest.InstallLocation))
                        gameChoices.Add(("Epic", manifest.CatalogNamespace, manifest.DisplayName, ProgramsToScan is not null && ProgramsToScan.Any(p => p.id == manifest.CatalogNamespace)));
            if (gameChoices.Any())
            {
                using SelectDialogForm form = new(this);
                List<(string platform, string id, string name)> choices = form.QueryUser("Choose which programs/games to scan for DLC:", gameChoices);
                scan = choices is not null && choices.Any();
                string retry = "\n\nPress the \"Rescan Programs / Games\" button to re-choose.";
                if (scan)
                {
                    ProgramsToScan = choices;
                    noneFoundLabel.Text = "None of the chosen programs/games were CreamAPI-applicable or ScreamAPI-applicable!" + retry;
                }
                else
                    noneFoundLabel.Text = "You didn't choose any programs/games!" + retry;
            }
            else
                noneFoundLabel.Text = "No CreamAPI-applicable or ScreamAPI-applicable programs/games were found on your computer!";
        }

        if (scan)
        {
            bool setup = true;
            int maxProgress = 0;
            int curProgress = 0;
            Progress<int> progress = new();
            IProgress<int> iProgress = progress;
            progress.ProgressChanged += (sender, _progress) =>
            {
                if (Program.Canceled) return;
                Thread.Sleep(0);
                if (_progress < 0 || _progress > maxProgress) maxProgress = -_progress;
                else curProgress = _progress;
                int p = Math.Max(Math.Min((int)((float)(curProgress / (float)maxProgress) * 100), 100), 0);
                progressLabel.Text = setup ? $"Setting up SteamCMD . . . {p}%"
                    : $"Gathering and caching your applicable games and their DLCs . . . {p}%";
                progressBar.Value = p;
            };
            if (Directory.Exists(SteamLibrary.InstallPath) && ProgramsToScan is not null && ProgramsToScan.Any(c => c.platform == "Steam"))
            {
                progressLabel.Text = $"Setting up SteamCMD . . . ";
                await SteamCMD.Setup(iProgress);
            }
            setup = false;
            progressLabel.Text = "Gathering and caching your applicable games and their DLCs . . . ";
            ProgramSelection.ValidateAll(ProgramsToScan);
            TreeNodes.ForEach(node => node.Remove());
            Thread.Sleep(0);
            await GetApplicablePrograms(iProgress);
            await SteamCMD.Cleanup();
        }

        HideProgressBar();
        selectionTreeView.Enabled = ProgramSelection.All.Any();
        allCheckBox.Enabled = selectionTreeView.Enabled;
        noneFoundLabel.Visible = !selectionTreeView.Enabled;
        installButton.Enabled = ProgramSelection.AllEnabled.Any();
        uninstallButton.Enabled = installButton.Enabled;
        cancelButton.Enabled = false;
        scanButton.Enabled = true;
        blockedGamesCheckBox.Enabled = true;
        blockProtectedHelpButton.Enabled = true;
    }

    private void OnTreeViewNodeCheckedChanged(object sender, TreeViewEventArgs e)
    {
        if (e.Action == TreeViewAction.Unknown) return;
        TreeNode node = e.Node;
        if (node is null) return;
        SyncNode(node);
        SyncNodeAncestors(node);
        SyncNodeDescendants(node);
        allCheckBox.CheckedChanged -= OnAllCheckBoxChanged;
        allCheckBox.Checked = TreeNodes.TrueForAll(treeNode => treeNode.Checked);
        allCheckBox.CheckedChanged += OnAllCheckBoxChanged;
        installButton.Enabled = ProgramSelection.AllEnabled.Any();
        uninstallButton.Enabled = installButton.Enabled;
    }

    private static void SyncNodeAncestors(TreeNode node)
    {
        TreeNode parentNode = node.Parent;
        if (parentNode is not null)
        {
            parentNode.Checked = parentNode.Nodes.Cast<TreeNode>().Any(childNode => childNode.Checked);
            SyncNodeAncestors(parentNode);
        }
    }

    private static void SyncNodeDescendants(TreeNode node) =>
        node.Nodes.Cast<TreeNode>().ToList().ForEach(childNode =>
        {
            childNode.Checked = node.Checked;
            SyncNode(childNode);
            SyncNodeDescendants(childNode);
        });

    private static void SyncNode(TreeNode node)
    {
        (string gameId, (DlcType type, string name, string icon) app)? dlc = ProgramSelection.GetDlcFromId(node.Name);
        if (dlc.HasValue)
        {
            (string gameId, _) = dlc.Value;
            ProgramSelection selection = ProgramSelection.FromId(gameId);
            if (selection is not null)
                selection.ToggleDlc(node.Name, node.Checked);
        }
        else
        {
            ProgramSelection selection = ProgramSelection.FromId(node.Name);
            if (selection is not null)
                selection.Enabled = node.Checked;
        }
    }

    internal List<TreeNode> TreeNodes => GatherTreeNodes(selectionTreeView.Nodes);
    private List<TreeNode> GatherTreeNodes(TreeNodeCollection nodeCollection)
    {
        List<TreeNode> treeNodes = new();
        foreach (TreeNode rootNode in nodeCollection)
        {
            treeNodes.Add(rootNode);
            treeNodes.AddRange(GatherTreeNodes(rootNode.Nodes));
        }
        return treeNodes;
    }

    private void ShowProgressBar()
    {
        progressBar.Value = 0;
        progressLabelGames.Text = "Loading . . . ";
        progressLabel.Visible = true;
        progressLabelGames.Text = "";
        progressLabelGames.Visible = true;
        progressLabelDLCs.Text = "";
        progressLabelDLCs.Visible = true;
        progressBar.Visible = true;
        groupBox1.Size = new(groupBox1.Size.Width, groupBox1.Size.Height - 3
            - progressLabel.Size.Height
            - progressLabelGames.Size.Height
            - progressLabelDLCs.Size.Height
            - progressBar.Size.Height);
    }
    private void HideProgressBar()
    {
        progressBar.Value = 100;
        progressLabel.Visible = false;
        progressLabelGames.Visible = false;
        progressLabelDLCs.Visible = false;
        progressBar.Visible = false;
        groupBox1.Size = new(groupBox1.Size.Width, groupBox1.Size.Height + 3
            + progressLabel.Size.Height
            + progressLabelGames.Size.Height
            + progressLabelDLCs.Size.Height
            + progressBar.Size.Height);
    }

    private void OnLoad(object sender, EventArgs _)
    {
    retry:
        try
        {
            HideProgressBar();
            selectionTreeView.AfterCheck += OnTreeViewNodeCheckedChanged;
            selectionTreeView.NodeMouseClick += (sender, e) =>
            {
                TreeNode node = e.Node;
                if (node is null || !node.Bounds.Contains(e.Location) || e.Button != MouseButtons.Right || e.Clicks != 1)
                    return;
                ContextMenuStrip contextMenuStrip = new();
                selectionTreeView.SelectedNode = node;
                string id = node.Name;
                ProgramSelection selection = ProgramSelection.FromId(id);
                (string gameAppId, (DlcType type, string name, string icon) app)? dlc = null;
                if (selection is null) dlc = ProgramSelection.GetDlcFromId(id);
                ProgramSelection dlcParentSelection = null;
                if (dlc is not null) dlcParentSelection = ProgramSelection.FromId(dlc.Value.gameAppId);
                if (selection is null && dlcParentSelection is null)
                    return;
                ContextMenuItem header = null;
                if (id == "ParadoxLauncher")
                    header = new(node.Text, "Paradox Launcher");
                else if (selection is not null)
                    header = new(node.Text, (id, selection.IconUrl, false));
                else if (dlc is not null)
                    header = new(node.Text, (id, dlc.Value.app.icon, false), (id, dlcParentSelection.IconUrl, false));
                contextMenuStrip.Items.Add(header ?? new ContextMenuItem(node.Text));
                string appInfoVDF = $@"{SteamCMD.AppInfoPath}\{id}.vdf";
                string appInfoJSON = $@"{SteamCMD.AppInfoPath}\{id}.json";
                string cooldown = $@"{ProgramData.CooldownPath}\{id}.txt";
                if ((File.Exists(appInfoVDF) || File.Exists(appInfoJSON)) && (selection is not null || dlc is not null))
                {
                    List<ContextMenuItem> queries = new();
                    if (File.Exists(appInfoJSON))
                    {
                        string platform = (selection is null || selection.IsSteam) ? "Steam Store " : selection.IsEpic ? "Epic GraphQL " : "";
                        queries.Add(new ContextMenuItem($"Open {platform}Query", "Notepad",
                            new EventHandler((sender, e) => Diagnostics.OpenFileInNotepad(appInfoJSON))));
                    }
                    if (File.Exists(appInfoVDF))
                        queries.Add(new ContextMenuItem("Open SteamCMD Query", "Notepad",
                            new EventHandler((sender, e) => Diagnostics.OpenFileInNotepad(appInfoVDF))));
                    if (queries.Any())
                    {
                        contextMenuStrip.Items.Add(new ToolStripSeparator());
                        foreach (ContextMenuItem query in queries)
                            contextMenuStrip.Items.Add(query);
                        contextMenuStrip.Items.Add(new ContextMenuItem("Refresh Queries", "Command Prompt",
                            new EventHandler((sender, e) =>
                            {
                                try
                                {
                                    File.Delete(appInfoVDF);
                                }
                                catch { }
                                try
                                {
                                    File.Delete(appInfoJSON);
                                }
                                catch { }
                                try
                                {
                                    File.Delete(cooldown);
                                }
                                catch { }
                                OnLoad(forceScan: true);
                            })));
                    }
                }
                if (selection is not null)
                {
                    if (id == "ParadoxLauncher")
                    {
                        contextMenuStrip.Items.Add(new ToolStripSeparator());
                        contextMenuStrip.Items.Add(new ContextMenuItem("Repair", "Command Prompt",
                            new EventHandler(async (sender, e) => await ParadoxLauncher.Repair(this, selection))));
                    }
                    contextMenuStrip.Items.Add(new ToolStripSeparator());
                    contextMenuStrip.Items.Add(new ContextMenuItem("Open Root Directory", "File Explorer",
                        new EventHandler((sender, e) => Diagnostics.OpenDirectoryInFileExplorer(selection.RootDirectory))));
                    List<string> directories = selection.DllDirectories.ToList();
                    if (selection.IsSteam)
                        for (int i = 0; i < directories.Count; i++)
                        {
                            string directory = directories[i];
                            directory.GetCreamApiComponents(out string sdk32, out string sdk32_o, out string sdk64, out string sdk64_o, out string config);
                            if (File.Exists(sdk32) || File.Exists(sdk32_o) || File.Exists(sdk64) || File.Exists(sdk64_o) || File.Exists(config))
                            {
                                contextMenuStrip.Items.Add(new ContextMenuItem($"Open Steamworks SDK Directory #{i + 1}", "File Explorer",
                                    new EventHandler((sender, e) => Diagnostics.OpenDirectoryInFileExplorer(directory))));
                            }
                        }
                    if (selection.IsEpic)
                        for (int i = 0; i < directories.Count; i++)
                        {
                            string directory = directories[i];
                            directory.GetScreamApiComponents(out string sdk32, out string sdk32_o, out string sdk64, out string sdk64_o, out string config);
                            if (File.Exists(sdk32) || File.Exists(sdk32_o) || File.Exists(sdk64) || File.Exists(sdk64_o) || File.Exists(config))
                            {
                                contextMenuStrip.Items.Add(new ContextMenuItem($"Open Epic Online Services SDK Directory #{i + 1}", "File Explorer",
                                    new EventHandler((sender, e) => Diagnostics.OpenDirectoryInFileExplorer(directory))));
                            }
                        }
                }
                if (id != "ParadoxLauncher")
                {
                    if (selection is not null && selection.IsSteam || dlcParentSelection is not null && dlcParentSelection.IsSteam)
                    {
                        contextMenuStrip.Items.Add(new ToolStripSeparator());
                        contextMenuStrip.Items.Add(new ContextMenuItem("Open SteamDB", "SteamDB",
                            new EventHandler((sender, e) => Diagnostics.OpenUrlInInternetBrowser("https://steamdb.info/app/" + id))));
                    }
                    if (selection is not null)
                    {
                        if (selection.IsSteam)
                        {
                            contextMenuStrip.Items.Add(new ContextMenuItem("Open Steam Store", "Steam Store",
                                new EventHandler((sender, e) => Diagnostics.OpenUrlInInternetBrowser(selection.ProductUrl))));
                            contextMenuStrip.Items.Add(new ContextMenuItem("Open Steam Community", (id, selection.SubIconUrl, true), "Steam Community",
                                new EventHandler((sender, e) => Diagnostics.OpenUrlInInternetBrowser("https://steamcommunity.com/app/" + id))));
                        }
                        else if (selection.IsEpic)
                        {
                            contextMenuStrip.Items.Add(new ToolStripSeparator());
                            contextMenuStrip.Items.Add(new ContextMenuItem("Open ScreamDB", "ScreamDB",
                                new EventHandler((sender, e) => Diagnostics.OpenUrlInInternetBrowser("https://scream-db.web.app/offers/" + id))));
                            contextMenuStrip.Items.Add(new ContextMenuItem("Open Epic Games Store", "Epic Games",
                                new EventHandler((sender, e) => Diagnostics.OpenUrlInInternetBrowser(selection.ProductUrl))));
                        }
                    }
                }
                contextMenuStrip.Show(selectionTreeView, e.Location);
            };
            OnLoad(forceProvideChoices: true);
        }
        catch (Exception e)
        {
            if (e.HandleException(form: this)) goto retry;
            Close();
        }
    }

    private void OnAccept(bool uninstall = false)
    {
        if (ProgramSelection.All.Any())
        {
            foreach (ProgramSelection selection in ProgramSelection.AllEnabled)
                if (!Program.IsProgramRunningDialog(this, selection)) return;
            if (ParadoxLauncher.DlcDialog(this)) return;
            Hide();
            using InstallForm installForm = new(this, uninstall);
            installForm.ShowDialog();
            if (installForm.Reselecting)
            {
                InheritLocation(installForm);
                Show();
                OnLoad();
            }
            else Close();
        }
    }

    private void OnInstall(object sender, EventArgs e) => OnAccept(false);

    private void OnUninstall(object sender, EventArgs e) => OnAccept(true);

    private void OnScan(object sender, EventArgs e) => OnLoad(forceProvideChoices: true);

    private void OnCancel(object sender, EventArgs e)
    {
        progressLabel.Text = "Cancelling . . . ";
        Program.Cleanup();
    }

    private void OnAllCheckBoxChanged(object sender, EventArgs e)
    {
        bool shouldCheck = false;
        TreeNodes.ForEach(node =>
        {
            if (node.Parent is null)
            {
                if (!node.Checked) shouldCheck = true;
                if (node.Checked != shouldCheck)
                {
                    node.Checked = shouldCheck;
                    OnTreeViewNodeCheckedChanged(null, new(node, TreeViewAction.ByMouse));
                }
            }
        });
        allCheckBox.Checked = shouldCheck;
    }

    private void OnBlockProtectedGamesCheckBoxChanged(object sender, EventArgs e)
    {
        Program.BlockProtectedGames = blockedGamesCheckBox.Checked;
        OnLoad(forceProvideChoices: true);
    }

    private readonly string helpButtonListPrefix = "\n    •  ";
    private void OnBlockProtectedGamesHelpButtonClicked(object sender, EventArgs e)
    {
        string blockedGames = "";
        foreach (string name in Program.ProtectedGames)
            blockedGames += helpButtonListPrefix + name;
        string blockedDirectories = "";
        foreach (string path in Program.ProtectedGameDirectories)
            blockedDirectories += helpButtonListPrefix + path;
        string blockedDirectoryExceptions = "";
        foreach (string name in Program.ProtectedGameDirectoryExceptions)
            blockedDirectoryExceptions += helpButtonListPrefix + name;
        using DialogForm form = new(this);
        form.Show(SystemIcons.Information,
            "Blocks the program from caching and displaying games protected by DLL checks," +
            "\nanti-cheats, or that are confirmed not to be working with CreamAPI or ScreamAPI." +
            "\n\nBlocked games:" + blockedGames +
            "\n\nBlocked game sub-directories:" + blockedDirectories +
            "\n\nBlocked game sub-directory exceptions (not blocked):" + blockedDirectoryExceptions,
            "OK", customFormText: "Block Protected Games");
    }
}
