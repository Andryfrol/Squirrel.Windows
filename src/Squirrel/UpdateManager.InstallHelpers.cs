﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using NuGet;
using Splat;

namespace Squirrel
{
    public sealed partial class UpdateManager
    {
        internal class InstallHelperImpl : IEnableLogger
        {
            readonly string applicationName;
            readonly string rootAppDirectory;

            public InstallHelperImpl(string applicationName, string rootAppDirectory)
            {
                this.applicationName = applicationName;
                this.rootAppDirectory = rootAppDirectory;
            }

            const string currentVersionRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            const string uninstallRegSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            public async Task<RegistryKey> CreateUninstallerRegistryEntry(string uninstallCmd, string quietSwitch)
            {
                var releaseContent = File.ReadAllText(Path.Combine(rootAppDirectory, "packages", "RELEASES"), Encoding.UTF8);
                var releases = ReleaseEntry.ParseReleaseFile(releaseContent);
                var latest = releases.OrderByDescending(x => x.Version).First();

                // Download the icon and PNG => ICO it. If this doesn't work, who cares
                var pkgPath = Path.Combine(rootAppDirectory, "packages", latest.Filename);
                var zp = new ZipPackage(pkgPath);
                    
                var targetPng = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
                var targetIco = Path.Combine(rootAppDirectory, "app.ico");

                // NB: Sometimes the Uninstall key doesn't exist
                using (var parentKey =
                    RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                        .CreateSubKey("Uninstall", RegistryKeyPermissionCheck.ReadWriteSubTree)) { ; }

                var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                    .CreateSubKey(uninstallRegSubKey + "\\" + applicationName, RegistryKeyPermissionCheck.ReadWriteSubTree);

                if (!File.Exists(targetIco)) {
                    try {
                        var wc = new WebClient();

                        await wc.DownloadFileTaskAsync(zp.IconUrl, targetPng);
                        using (var fs = new FileStream(targetIco, FileMode.Create)) {
                            if (zp.IconUrl.AbsolutePath.EndsWith("ico")) {
                                var bytes = File.ReadAllBytes(targetPng);
                                fs.Write(bytes, 0, bytes.Length);
                            } else {
                                using (var bmp = (Bitmap)Image.FromFile(targetPng))
                                using (var ico = Icon.FromHandle(bmp.GetHicon())) {
                                    ico.Save(fs);
                                }
                            }

                            key.SetValue("DisplayIcon", targetIco, RegistryValueKind.String);
                        }
                    } catch(Exception ex) {
                        this.Log().InfoException("Couldn't write uninstall icon, don't care", ex);
                    } finally {
                        File.Delete(targetPng);
                    }
                }

                var stringsToWrite = new[] {
                    new { Key = "DisplayName", Value = zp.Title ?? zp.Description ?? zp.Summary },
                    new { Key = "DisplayVersion", Value = zp.Version.ToString() },
                    new { Key = "InstallDate", Value = DateTime.Now.ToString("yyyymmdd") },
                    new { Key = "InstallLocation", Value = rootAppDirectory },
                    new { Key = "Publisher", Value = zp.Authors.First() },
                    new { Key = "QuietUninstallString", Value = String.Format("{0} {1}", uninstallCmd, quietSwitch) },
                    new { Key = "UninstallString", Value = uninstallCmd },
                    new { Key = "URLUpdateInfo", Value = zp.ProjectUrl != null ? zp.ProjectUrl.ToString() : "", }
                };

                var dwordsToWrite = new[] {
                    new { Key = "EstimatedSize", Value = (int)((new FileInfo(pkgPath)).Length / 1024) },
                    new { Key = "NoModify", Value = 1 },
                    new { Key = "NoRepair", Value = 1 },
                    new { Key = "Language", Value = 0x0409 },
                };

                foreach (var kvp in stringsToWrite) {
                    key.SetValue(kvp.Key, kvp.Value, RegistryValueKind.String);
                }
                foreach (var kvp in dwordsToWrite) {
                    key.SetValue(kvp.Key, kvp.Value, RegistryValueKind.DWord);
                }

                return key;
            }

            public void RemoveUninstallerRegistryEntry()
            {
                var key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)
                    .OpenSubKey(uninstallRegSubKey, true);
                key.DeleteSubKeyTree(applicationName);
            }

            public void CreateShortcutsForExecutable(string exeName, ShortcutLocation locations)
            {
                var releases = Utility.LoadLocalReleases(Utility.LocalReleaseFileForAppDir(rootAppDirectory));
                var thisRelease = Utility.FindCurrentVersion(releases);

                var zf = new ZipPackage(thisRelease.Filename);
                var exePath = Path.Combine(Utility.AppDirForRelease(rootAppDirectory, thisRelease), exeName);
                var fileVerInfo = FileVersionInfo.GetVersionInfo(exePath);

                foreach (var f in new[] { ShortcutLocation.StartMenu, ShortcutLocation.Desktop, }) {
                    if (!locations.HasFlag(f)) continue;

                    var file = linkTargetForVersionInfo(f, zf, fileVerInfo);
                    if (File.Exists(file)) File.Delete(file);

                    var sl = new ShellLink {
                        Target = exePath,
                        IconPath = exePath,
                        IconIndex = 0,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        Description = zf.Description,
                    };

                    sl.Save(file);
                }
            }

            public void RemoveShortcutsForExecutable(string exeName, ShortcutLocation locations)
            {
                var releases = Utility.LoadLocalReleases(Utility.LocalReleaseFileForAppDir(rootAppDirectory));
                var thisRelease = Utility.FindCurrentVersion(releases);

                var zf = new ZipPackage(thisRelease.Filename);
                var fileVerInfo = FileVersionInfo.GetVersionInfo(
                    Path.Combine(Utility.AppDirForRelease(rootAppDirectory, thisRelease), exeName));

                foreach (var f in new[] { ShortcutLocation.StartMenu, ShortcutLocation.Desktop, }) {
                    if (!locations.HasFlag(f)) continue;

                    var file = linkTargetForVersionInfo(f, zf, fileVerInfo);
                    if (File.Exists(file)) File.Delete(file);
                }
            }

            string linkTargetForVersionInfo(ShortcutLocation location, IPackage package, FileVersionInfo versionInfo)
            {
                return getLinkTarget(location, package.Title, versionInfo.ProductName);
            }

            string getLinkTarget(ShortcutLocation location, string title, string applicationName, bool createDirectoryIfNecessary = true)
            {
                var dir = default(string);

                switch (location) {
                case ShortcutLocation.Desktop:
                    dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    break;
                case ShortcutLocation.StartMenu:
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), applicationName);
                    break;
                }

                if (createDirectoryIfNecessary && Directory.Exists(dir)) {
                    Directory.CreateDirectory(dir);
                }

                return Path.Combine(dir, title + ".lnk");
            }
        }
    }
}
