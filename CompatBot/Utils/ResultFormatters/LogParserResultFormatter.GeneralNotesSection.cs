﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CompatApiClient.Utils;
using CompatBot.EventHandlers;
using CompatBot.EventHandlers.LogParsing.POCOs;
using DSharpPlus;
using DSharpPlus.Entities;
using IrdLibraryClient.IrdFormat;

namespace CompatBot.Utils.ResultFormatters
{
    internal static partial class LogParserResult
    {
        private static async Task BuildNotesSectionAsync(DiscordEmbedBuilder builder, LogParseState state, NameValueCollection items, DiscordClient discordClient)
        {
            BuildWeirdSettingsSection(builder, items);
            BuildMissingLicensesSection(builder, items);
            var (irdChecked, brokenDump, longestPath) = await HasBrokenFilesAsync(items).ConfigureAwait(false);
            brokenDump |= !string.IsNullOrEmpty(items["edat_block_offset"]);
            var elfBootPath = items["elf_boot_path"] ?? "";
            var isEboot = !string.IsNullOrEmpty(elfBootPath) && elfBootPath.EndsWith("EBOOT.BIN", StringComparison.InvariantCultureIgnoreCase);
            var isElf = !string.IsNullOrEmpty(elfBootPath) && !elfBootPath.EndsWith("EBOOT.BIN", StringComparison.InvariantCultureIgnoreCase);
            var notes = new List<string>();
            var serial = items["serial"] ?? "";
            if (items["fatal_error"] is string fatalError)
            {
                var context = items["fatal_error_context"] ?? "";
                builder.AddField("Fatal Error", $"```\n{fatalError.Trim(1020)}\n```");
                if (fatalError.Contains("psf.cpp", StringComparison.InvariantCultureIgnoreCase)
                    || fatalError.Contains("invalid map<K, T>", StringComparison.InvariantCultureIgnoreCase)
                    || context.Contains("SaveData", StringComparison.InvariantCultureIgnoreCase))
                    notes.Add("❌ Game save data is corrupted");
                else if (fatalError.Contains("Could not bind OpenGL context"))
                    notes.Add("❌ GPU or installed GPU drivers do not support OpenGL 4.3");
                else if (fatalError.Contains("file is null"))
                {
                    if (context.StartsWith("RSX", StringComparison.InvariantCultureIgnoreCase) || fatalError.StartsWith("RSX:"))
                        notes.Add("❌ Shader cache might be corrupted; right-click on the game, then `Remove` → `Shader Cache`");
                    if (context.StartsWith("SPU", StringComparison.InvariantCultureIgnoreCase))
                        notes.Add("❌ SPU cache might be corrupted; right-click on the game, then `Remove` → `SPU Cache`");
                    if (context.StartsWith("PPU", StringComparison.InvariantCultureIgnoreCase))
                        notes.Add("❌ PPU cache might be corrupted; right-click on the game, then `Remove` → `PPU Cache`");
                }
                else if (fatalError.Contains("(e=0x17): file::read"))
                {
                    // on windows this is ERROR_CRC
                    notes.Add("❌ Storage device communication error; check your cables");
                }
                else if (fatalError.Contains("Unknown primitive type"))
                {
                    notes.Add("⚠ RSX desync detected, it's probably random");
                }
            }
            else if (items["unimplemented_syscall"] is string unimplementedSyscall)
            {
                if (unimplementedSyscall.Contains("syscall_988"))
                {
                    fatalError = "Unimplemented syscall " + unimplementedSyscall;
                    builder.AddField("Fatal Error", $"```{fatalError.Trim(1022)}```");
                    if (items["ppu_decoder"] is string ppuDecoder && ppuDecoder.Contains("Recompiler") && !Config.Colors.CompatStatusPlayable.Equals(builder.Color.Value))
                        notes.Add("⚠ PPU desync detected; check your save data for corruption and/or try PPU Interpreter");
                    else
                        notes.Add("⚠ PPU desync detected, most likely cause is corrupted save data");
                }
            }

            if (Config.Colors.CompatStatusNothing.Equals(builder.Color.Value) || Config.Colors.CompatStatusLoadable.Equals(builder.Color.Value))
                notes.Add("❌ This game doesn't work on the emulator yet");
            if (items["failed_to_decrypt"] is string _)
                notes.Add("❌ Failed to decrypt game content, license file might be corrupted");
            if (items["failed_to_boot"] is string _)
                notes.Add("❌ Failed to boot the game, the dump might be encrypted or corrupted");
            if (items["failed_to_verify"] is string verifyFails)
            {
                var types = verifyFails.Split(Environment.NewLine).Distinct().ToList();
                if (types.Contains("sce"))
                    notes.Add("❌ Failed to decrypt executables, PPU recompiler may crash or fail");
            }
            if (brokenDump)
                notes.Add("❌ Some game files are missing or corrupted, please re-dump and validate.");
            else if (irdChecked)
                notes.Add("✅ Checked missing files against IRD");
            if (items["fw_version_installed"] is string fw && !string.IsNullOrEmpty(fw))
            {
                if (Version.TryParse(fw, out var fwv))
                {
                    if (fwv < MinimumFirmwareVersion)
                        notes.Add($"⚠ Firmware version {MinimumFirmwareVersion} or later is recommended");
                }
                else
                    notes.Add("⚠ Custom firmware is not supported, please use the latest official one");
            }

            if (items["os_type"] == "Windows")
            {
                var knownPaths = new[]
                {
                    items["win_path"],
                    items["ldr_game_full"],
                    items["ldr_disc_full"],
                    items["ldr_path_full"],
                    items["ldr_boot_path_full"],
                    items["elf_boot_path_full"]
                }.Where(s => !string.IsNullOrEmpty(s));
                const int maxPath = 260;
                const int maxFolderPath = 260 - 1 - 8 - 3;
                foreach (var p in knownPaths)
                {
                    if (p.Length > maxPath)
                    {
                        notes.Add($"⚠ Some file paths are longer than {maxPath} characters");
                        break;
                    }
                    else
                    {
                        var baseDir = Path.GetDirectoryName(p) ?? p;
                        if (baseDir.Length > maxFolderPath)
                        {
                            notes.Add($"⚠ Some folder paths are longer than {maxFolderPath} characters");
                            break;
                        }
                        else if (baseDir.Length + longestPath > maxPath)
                        {
                            notes.Add($"⚠ Some file paths are potentially longer than {maxPath} characters");
                            break;
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(items["host_root_in_boot"]) && isEboot)
                notes.Add("❌ Retail game booted as an ELF through the `/root_host/`, probably due to passing path as an argument; please boot through the game library list for now");
            var path = items["ldr_game"] ?? items["ldr_path"] ?? items["ldr_boot_path"] ?? items["elf_boot_path"];
            if (!string.IsNullOrEmpty(path)
                && serial.StartsWith("NP")
                && items["ldr_game_serial"] != serial
                && items["ldr_path_serial"] != serial
                && items["ldr_boot_path_serial"] != serial
                && items["elf_boot_path_serial"] != serial)
                notes.Add("❌ Digital version of the game outside of `/dev_hdd0/game/` directory");
            // LDR: Path: before settings is unreliable, because you can boot through installed patch or game data
            if (!string.IsNullOrEmpty(items["ldr_disc"])
                && serial.StartsWith("BL")
                && !string.IsNullOrEmpty(items["ldr_disc_serial"]))
                notes.Add("❌ Disc version of the game inside the `/dev_hdd0/game/` directory");
            if (!string.IsNullOrEmpty(serial) && isElf)
                notes.Add($"⚠ Retail game booted directly through `{Path.GetFileName(elfBootPath)}`, which is not recommended");

            if (items["log_from_ui"] is string _)
                notes.Add("ℹ The log is a copy from UI, please upload the full file created by RPCS3");
            else if (string.IsNullOrEmpty(items["ppu_decoder"]) || string.IsNullOrEmpty(items["renderer"]))
            {
                notes.Add("ℹ The log is empty");
                notes.Add("ℹ Please boot the game and upload a new log");
            }
            else if (string.IsNullOrEmpty(serial + items["game_title"])
                     && !string.IsNullOrEmpty(items["fw_installed_message"])
                     && items["fw_version_installed"] is string fwVersion)
            {
                notes.Add($"ℹ The log contains only installation of firmware {fwVersion}");
                notes.Add("ℹ Please boot the game and upload a new log");
            }

            var category = items["game_category"];
            if (category == "PE"
                || category == "PP"
                || serial.StartsWith('U') && ProductCodeLookup.ProductCode.IsMatch(serial))
            {
                builder.Color = Config.Colors.CompatStatusNothing;
                notes.Add("❌ PSP software is not supported");
            }
            else if (category == "MN")
            {
                builder.Color = Config.Colors.CompatStatusNothing;
                notes.Add("❌ Minis are not supported");
            }
            if (category == "2G" || category == "2P" || category == "2D")
            {
                builder.Color = Config.Colors.CompatStatusNothing;
                notes.Add("❌ PS2 software is not supported");
            }

            if (items["compat_database_path"] is string compatDbPath
                && InstallPath.Match(compatDbPath.Replace('\\', '/').Replace("//", "/").Trim()) is Match installPathMatch
                && installPathMatch.Success)
            {
                var rpcs3FolderMissing = string.IsNullOrEmpty(installPathMatch.Groups["rpcs3_folder"].Value);
                var desktop = !string.IsNullOrEmpty(installPathMatch.Groups["desktop"].Value);
                var programFiles = !string.IsNullOrEmpty(installPathMatch.Groups["program_files"].Value);
                if (rpcs3FolderMissing)
                {
                    if (desktop)
                        notes.Add("ℹ RPCS3 installed directly on desktop, without folder");
                    else if (programFiles)
                        notes.Add("⚠ RPCS3 installed directly inside Program Files, without folder");
                    else
                        notes.Add("⚠ RPCS3 installed in the drive root, please create a folder and move all files inside");
                }
                if (programFiles)
                    notes.Add("⚠ Program Files have special permissions, please move RPCS3 to another location");
            }

            if (int.TryParse(items["thread_count"], out var threadCount) && threadCount < 4)
                notes.Add($"⚠ This CPU only has {threadCount} hardware thread{(threadCount == 1 ? "" : "s")} enabled");

            if (items["cpu_model"] is string cpu)
            {
                if (cpu.StartsWith("AMD"))
                {
                    if (cpu.Contains("Ryzen"))
                    {
                        if (threadCount < 12)
                            notes.Add("⚠ Six cores or more is recommended for Ryzen CPUs");
                        if (items["os_type"] != "Linux"
                            && items["thread_scheduler"] == DisabledMark)
                            notes.Add("⚠ Please enable `Thread scheduler` option in the CPU Settings");
                    }
                    else
                        notes.Add("⚠ AMD CPUs before Ryzen are too weak for PS3 emulation");
                }

                if (cpu.StartsWith("Intel"))
                {
                    if (!items["cpu_extensions"].Contains("TSX")
                        && (cpu.Contains("Core2")
                            || cpu.Contains("Celeron")
                            || cpu.Contains("Atom")
                            || cpu.Contains("Pentium")
                            || cpu.EndsWith('U')
                            || cpu.EndsWith('M')
                            || cpu.Contains('Y')
                            || ((cpu.EndsWith("HQ") || cpu.EndsWith('H'))
                                && threadCount < 8)))
                        notes.Add("⚠ This CPU is too old and/or too weak for PS3 emulation");
                }
            }

            var supportedGpu = true;
            Version oglVersion = null;
            if (items["opengl_version"] is string oglVersionString)
                Version.TryParse(oglVersionString, out oglVersion);
            if (items["glsl_version"] is string glslVersionString &&
                Version.TryParse(glslVersionString, out var glslVersion))
            {
                glslVersion = new Version(glslVersion.Major, glslVersion.Minor / 10);
                if (oglVersion == null || glslVersion > oglVersion)
                    oglVersion = glslVersion;
            }

            if (oglVersion != null)
            {
                if (oglVersion < MinimumOpenGLVersion)
                {
                    notes.Add($"❌ GPU only supports OpenGL {oglVersion.Major}.{oglVersion.Minor}, which is below the minimum requirement of {MinimumOpenGLVersion}");
                    supportedGpu = false;
                }
            }

            var gpuInfo = items["gpu_info"] ?? items["discrete_gpu_info"];
            if (supportedGpu && !string.IsNullOrEmpty(gpuInfo))
            {
                if (IntelGpuModel.Match(gpuInfo) is Match intelMatch
                    && intelMatch.Success)
                {
                    var modelNumber = intelMatch.Groups["gpu_model_number"].Value;
                    if (!string.IsNullOrEmpty(modelNumber) && modelNumber.StartsWith('P'))
                        modelNumber = modelNumber.Substring(1);
                    int.TryParse(modelNumber, out var modelNumberInt);
                    if (modelNumberInt < 500 || modelNumberInt > 1000)
                    {
                        notes.Add("⚠ Intel iGPUs before Skylake do not fully comply with OpenGL 4.3");
                        supportedGpu = false;
                    }
                    else
                        notes.Add("⚠ Intel iGPUs are not officially supported; visual glitches are to be expected");
                }

                if (items["driver_version_info"] is string driverVersionString)
                {
                    if (Version.TryParse(driverVersionString, out var driverVersion)
                        && Version.TryParse(items["build_version"], out var buildVersion)
                        && int.TryParse(items["build_number"], out var buildNumber))
                    {
                        buildVersion = new Version(buildVersion.Major, buildVersion.Minor, buildVersion.Build, buildNumber);
                        if (IsNvidia(gpuInfo))
                        {
                            if (driverVersion < NvidiaRecommendedOldWindowsVersion)
                                notes.Add($"❗ Please update your nVidia GPU driver to at least version {NvidiaRecommendedOldWindowsVersion}");
                            if (items["os_type"] is string os
                                && os != "Linux"
                                && buildVersion < NvidiaFullscreenBugFixed
                                && items["build_branch"] == "HEAD")
                            {
                                if (driverVersion >= NvidiaFullscreenBugMinVersion
                                    && driverVersion < NvidiaFullscreenBugMaxVersion
                                    && items["renderer"] == "Vulkan")
                                    notes.Add("ℹ 400 series nVidia drivers can cause screen freezes, please update RPCS3");
                            }
                        }
                        else if (IsAmd(gpuInfo))
                        {
                            if (driverVersion < AmdRecommendedOldWindowsVersion)
                                notes.Add($"❗ Please update your AMD GPU driver to at least version {AmdRecommendedOldWindowsVersion}");
                        }
                    }
                    else if (driverVersionString.Contains("older than", StringComparison.InvariantCultureIgnoreCase))
                    {
                        if (IsAmd(gpuInfo))
                            notes.Add($"❗ Please update your AMD GPU driver to at least version {AmdRecommendedOldWindowsVersion}");
                    }
                }
            }

            if (!string.IsNullOrEmpty(items["shader_compile_error"]))
            {
                if (supportedGpu)
                    notes.Add("❌ Shader compilation error might indicate shader cache corruption");
                else
                    notes.Add("❌ Shader compilation error on unsupported GPU");
            }

            if (!string.IsNullOrEmpty(items["enqueue_buffer_error"])
                && state.ValueHitStats.TryGetValue("enqueue_buffer_error", out var enqueueBufferErrorCount)
                && enqueueBufferErrorCount > 100)
            {
                if (items["os_type"] == "Windows")
                    notes.Add("⚠ Audio backend issues detected; it could be caused by a bad driver or 3rd party software");
                else
                    notes.Add("⚠ Audio backend issues detected; check for high audio driver/sink latency");
            }

            var ppuPatches = GetPatches(items["ppu_hash"], items["ppu_hash_patch"]);
            var ovlPatches = GetPatches(items["ovl_hash"], items["ovl_hash_patch"]);
            var spuPatches = GetPatches(items["spu_hash"], items["spu_hash_patch"]);
            if (ppuPatches.Any() || spuPatches.Any() || ovlPatches.Any())
            {
                var patchCount = "";
                if (ppuPatches.Count != 0)
                    patchCount += "PPU: " + string.Join('/', ppuPatches.Values) + ", ";
                if (ovlPatches.Count != 0)
                    patchCount += "OVL: " + string.Join('/', ovlPatches.Values) + ", ";
                if (spuPatches.Count != 0)
                    patchCount += "SPU: " + string.Join('/', spuPatches.Values);
                notes.Add($"ℹ Game-specific patches were applied ({patchCount.TrimEnd(',', ' ')})");
            }
            if (P5Ids.Contains(serial))
            {
                /*
                 * mod support = 27
                 * log access  = 39
                 * intro skip  = 1
                 * 60 fps v1   = 12
                 * 60 fps v2   = 268
                 * disable hud = 10
                 * random music= 19
                 * disable blur= 8
                 * distortion  = 8
                 * 100% dist   = 8
                 */
                if (ppuPatches.Values.Any(n => n > 260 || n == 27+12 || n == 12))
                    notes.Add("ℹ 60 fps patch is enabled; please disable if you have any strange issues");
                if (ppuPatches.Values.Any(n => n == 12 || n == 12+27))
                    notes.Add("⚠ An old version of the 60 fps patch is used");
            }
            if (items["ppu_hash"] is string ppuHashes
                && ppuHashes.Split(Environment.NewLine, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is string firstPpuHash
                && !string.IsNullOrEmpty(firstPpuHash))
            {
                var exe = Path.GetFileName(items["elf_boot_path"] ?? "");
                if (string.IsNullOrEmpty(exe) || exe.Equals("EBOOT.BIN", StringComparison.InvariantCultureIgnoreCase))
                    exe = "Main";
                else
                    exe = $"`{exe}`";
                notes.Add($"ℹ {exe} hash: `PPU-{firstPpuHash}`");
            }

            bool discInsideGame = false;
            bool discAsPkg = false;
            var pirateEmoji = discordClient.GetEmoji(":piratethink:", DiscordEmoji.FromUnicode("🔨"));
            //var thonkEmoji = discordClient.GetEmoji(":thonkang:", DiscordEmoji.FromUnicode("🤔"));
			// this is a common scenario now that Mega did the version merge from param.sfo
/*
            if (items["game_category"] == "GD")
                notes.Add($"❔ Game was booted through the Game Data");
*/
            if (category == "DG" || category == "GD") // only disc games should install game data
            {
                discInsideGame |= !string.IsNullOrEmpty(items["ldr_disc"]) && !(items["serial"]?.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase) ?? false);
                discAsPkg |= items["serial"]?.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase) ?? false;
                discAsPkg |= items["ldr_game_serial"] is string ldrGameSerial && ldrGameSerial.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase);
            }

            discAsPkg |= category == "HG" && !(items["serial"]?.StartsWith("NP", StringComparison.InvariantCultureIgnoreCase) ?? false);
            if (discInsideGame)
                notes.Add($"❌ Disc game inside `{items["ldr_disc"]}`");
            if (discAsPkg)
                notes.Add($"{pirateEmoji} Disc game installed as a PKG ");

            if (!string.IsNullOrEmpty(items["native_ui_input"]))
                notes.Add("⚠ Pad initialization problem detected; try disabling `Native UI`");
            if (!string.IsNullOrEmpty(items["xaudio_init_error"]))
                notes.Add("❌ XAudio initialization failed; make sure you have audio output device working");

            if (!string.IsNullOrEmpty(items["fw_missing_msg"])
                || !string.IsNullOrEmpty(items["fw_missing_something"]))
                notes.Add("❌ PS3 firmware is missing or corrupted");

            if (items["game_mod"] is string mod)
                notes.Add($"ℹ Game files modification present: `{mod.Trim(10)}`");

            var updateInfo = await CheckForUpdateAsync(items).ConfigureAwait(false);
            var buildBranch = items["build_branch"]?.ToLowerInvariant();
            if (updateInfo != null
                && (buildBranch == "head"
                    || buildBranch == "spu_perf"
                    || string.IsNullOrEmpty(buildBranch) && updateInfo.CurrentBuild != null))
            {
                string prefix = "⚠";
                string timeDeltaStr;
                if (updateInfo.GetUpdateDelta() is TimeSpan timeDelta)
                {
                    timeDeltaStr = timeDelta.AsTimeDeltaDescription() + " old";
                    if (timeDelta > PrehistoricBuild)
                        prefix = "😱";
                    else if (timeDelta > AncientBuild)
                        prefix = "💢";
                    //else if (timeDelta > VeryVeryOldBuild)
                    //    prefix = "💢";
                    else if (timeDelta > VeryOldBuild)
                        prefix = "‼";
                    else if (timeDelta > OldBuild)
                        prefix = "❗";
                }
                else
                    timeDeltaStr = "outdated";

                notes.Add($"{prefix} This RPCS3 build is {timeDeltaStr}, please consider updating it");
                if (buildBranch == "spu_perf")
                    notes.Add($"ℹ `{buildBranch}` build is obsolete, current master build offers at least the same level of performance and includes many additional improvements");
            }

            if (items["failed_pad"] is string failedPad)
                notes.Add($"❌ Binding `{failedPad.Sanitize(replaceBackTicks: true)}` failed, check if device is connected.");


            if (DesIds.Contains(serial))
                notes.Add("ℹ If you experience infinite load screen, clear game cache via `File` → `All games` → `Remove Disk Cache`");

            if (items["custom_config"] != null
                && (notes.Any() || items["weird_settings_notes"] is string _))
                notes.Add("⚠ To change custom configuration, **Right-click on the game**, then `Configure`");

            if (state.Error == LogParseState.ErrorCode.SizeLimit)
                notes.Add("ℹ The log was too large, so only the last processed run is shown");

            var notesContent = new StringBuilder();
            foreach (var line in SortLines(notes, pirateEmoji))
                notesContent.AppendLine(line);
            PageSection(builder, notesContent.ToString().Trim(), "Notes");
        }

        private static void BuildMissingLicensesSection(DiscordEmbedBuilder builder, NameValueCollection items)
        {
            if (items["rap_file"] is string rap)
            {
                var limitTo = 5;
                var licenseNames = rap.Split(Environment.NewLine)
                    .Distinct()
                    .Select(Path.GetFileName)
                    .Distinct()
                    .Except(KnownBogusLicenses)
                    .Select(p => $"{StringUtils.InvisibleSpacer}`{p}`")
                    .ToList();
                if (licenseNames.Count == 0)
                    return;

                string content;
                if (licenseNames.Count > limitTo)
                {
                    content = string.Join(Environment.NewLine, licenseNames.Take(limitTo - 1));
                    var other = licenseNames.Count - limitTo + 1;
                    content += $"{Environment.NewLine}and {other} other license{StringUtils.GetSuffix(other)}";
                }
                else
                    content = string.Join(Environment.NewLine, licenseNames);

                builder.AddField("Missing Licenses", content);
            }
        }

        private static async Task<(bool irdChecked, bool broken, int longestPath)> HasBrokenFilesAsync(NameValueCollection items)
        {
            var defaultLongestPath = "/PS3_GAME/USRDIR/".Length + (1+8+3)*2; // usually there's at least one more level for data files
            if (!(items["serial"] is string productCode))
                return (false, false, defaultLongestPath);

            if (!productCode.StartsWith("B") && !productCode.StartsWith("M"))
                return (false, false, defaultLongestPath);

            HashSet<string> knownFiles;
            try
            {
                var irdFiles = await irdClient.DownloadAsync(productCode, Config.IrdCachePath, Config.Cts.Token).ConfigureAwait(false);
                knownFiles = new HashSet<string>(
                    from ird in irdFiles
                    from name in ird.GetFilenames()
                    select name,
                    StringComparer.InvariantCultureIgnoreCase
                );
            }
            catch (Exception e)
            {
                Config.Log.Warn(e, "Failed to get IRD files for " + productCode);
                return (false, false, defaultLongestPath);
            }
            if (knownFiles.Count == 0)
                return (false, false, defaultLongestPath);

            var longestPath = knownFiles.Max(p => p.TrimEnd('.').Length);
            if (string.IsNullOrEmpty(items["broken_directory"])
                && string.IsNullOrEmpty(items["broken_filename"]))
                return (false, false, longestPath);

            var missingDirs = items["broken_directory"]?.Split(Environment.NewLine).Distinct().ToList() ??
                              new List<string>(0);
            var missingFiles = items["broken_filename"]?.Split(Environment.NewLine).Distinct().ToList() ??
                               new List<string>(0);

            var broken = missingFiles.Where(knownFiles.Contains).ToList();
            if (broken.Count > 0)
            {
                Config.Log.Debug("List of broken files according to IRD:");
                foreach (var f in broken)
                    Config.Log.Debug(f);
                return (true, true, longestPath);
            }

            var knownDirs = new HashSet<string>(knownFiles.Select(f => Path.GetDirectoryName(f).Replace('\\', '/')),
                StringComparer.InvariantCultureIgnoreCase);
            var brokenDirs = missingDirs.Where(knownDirs.Contains).ToList();
            if (brokenDirs.Count > 0)
            {
                Config.Log.Debug("List of broken directories according to IRD:");
                foreach (var d in broken)
                    Config.Log.Debug(d);
                return (true, true, longestPath);
            }
            return (true, false, longestPath);
        }
    }
}
