// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Ported from Microsoft PowerToys (https://github.com/microsoft/PowerToys)
// Original: src/settings-ui/Settings.UI/SettingsXAML/Controls/WelcomeHero/WelcomeHeroModules.cs
// License: MIT (Copyright (c) Microsoft Corporation). See THIRD-PARTY-NOTICES in the repo for details.
using System.Collections.Generic;

namespace Easy4K.Controls.WelcomeHero
{
    /// <summary>
    /// The curated set of modules that warp into the Welcome hero.
    /// The order matters: the first modules land closest to the logo.
    /// </summary>
    /// <remarks>
    /// The icons live in <c>Assets/Settings/Modules/OOBE/Welcome/</c>; they were copied from the PowerToys
    /// repository (<c>doc/images/icons</c>), see the provenance header of this file.
    /// </remarks>
    public static class WelcomeHeroModules
    {
        public const string AssetFolder = "Assets/Settings/Modules/OOBE/Welcome/";

        public const string LogoAssetName = "Easy4K";

        public static IReadOnlyList<WelcomeHeroModule> All { get; } =
        [
            new("CommandPalette", "CmdPal", "Shell_CmdPal/Content"),
            new("AdvancedPaste", "AdvancedPaste", "Shell_AdvancedPaste/Content"),
            new("FancyZones", "FancyZones", "Shell_FancyZones/Content"),
            new("ColorPicker", "ColorPicker", "Shell_ColorPicker/Content"),
            new("PowerRename", "PowerRename", "Shell_PowerRename/Content"),
            new("Peek", "Peek", "Shell_Peek/Content"),
            new("Workspaces", "Workspaces", "Shell_Workspaces/Content"),
            new("LightSwitch", "LightSwitch", "Shell_LightSwitch/Content"),
            new("TextExtractor", "TextExtractor", "Shell_TextExtractor/Content"),
            new("ZoomIt", "ZoomIt", "Shell_ZoomIt/Content"),
            new("KeyboardManager", "KBM", "Shell_KeyboardManager/Content"),
            new("EnvironmentVariables", "EnvironmentVariables", "Shell_EnvironmentVariables/Content"),
            new("ImageResizer", "ImageResizer", "Shell_ImageResizer/Content"),
            new("AlwaysOnTop", "AlwaysOnTop", "Shell_AlwaysOnTop/Content"),
            new("FileLocksmith", "FileLocksmith", "Shell_FileLocksmith/Content"),
            new("NewPlus", "NewPlus", "NewPlus_Product_Name/Content"),
            new("PowerDisplay", "PowerDisplay", "Shell_PowerDisplay/Content"),
            new("MouseWithoutBorders", "MouseWithoutBorders", "Shell_MouseWithoutBorders/Content"),
            new("Awake", "Awake", "Shell_Awake/Content"),
            new("CropAndLock", "CropAndLock", "Shell_CropAndLock/Content"),
            new("MeasureTool", "MeasureTool", "Shell_MeasureTool/Content"),
            new("ShortcutGuide", "ShortcutGuide", "Shell_ShortcutGuide/Content"),
            new("QuickAccent", "QuickAccent", "Shell_QuickAccent/Content"),
            new("Hosts", "Hosts", "Shell_Hosts/Content"),
            new("RegistryPreview", "RegistryPreview", "Shell_RegistryPreview/Content"),
            new("CommandNotFound", "CmdNotFound", "Shell_CmdNotFound/Content"),
            new("FileExplorerPreview", "FileExplorer", "Shell_PowerPreview/Content"),
            new("WindowHopper", "AltWindowCycle", "Shell_AltWindowCycle/Content"),
            new("GrabAndMove", "GrabAndMove", "Shell_GrabAndMove/Content"),
            new("PowerToysRun", "Run", "Shell_PowerLauncher/Content"),
            new("FindMyMouse", "MouseUtils", "MouseUtils_FindMyMouse/Header"),
            new("MouseJump", "MouseUtils", "MouseUtils_MouseJump/Header"),
            new("MouseHighlighter", "MouseUtils", "MouseUtils_MouseHighlighter/Header"),
            new("MouseCrosshairs", "MouseUtils", "MouseUtils_MousePointerCrosshairs/Header"),
            new("CursorWrap", "MouseUtils", "MouseUtils_CursorWrap/Header"),
        ];

        public static string GetAssetUri(string assetName) => $"ms-appx:///{AssetFolder}{assetName}.png";
    }
}
