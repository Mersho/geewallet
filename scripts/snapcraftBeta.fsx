#!/usr/bin/env fsharpi

open System
open System.IO

open System.Text.RegularExpressions

#load "fsxHelper.fs"
open GWallet.Scripting

let overrideVersion = Environment.GetEnvironmentVariable "OVERRIDE_SNAP_VERSION"
if not (String.IsNullOrWhiteSpace overrideVersion) then
    let snapcraftDir = Path.Combine(FsxHelper.RootDir.FullName, "snap", "snapcraft.yaml")
    let readSnapcraftYaml = File.ReadAllText snapcraftDir
    let matchVersion = Regex.Match(readSnapcraftYaml, @"(?<=version:\s')[\d\.]+(?=')")
    if matchVersion.Success then
        let versionList = matchVersion.Value.Split('.')
        Array.set versionList 1 ((int versionList.[1] + 2).ToString())
        let newSnapYaml = readSnapcraftYaml.Replace (matchVersion.Value, String.Join(".", versionList))
        File.WriteAllText (snapcraftDir, newSnapYaml)
    else
        failwithf "Snap version not found in: '%s'" snapcraftDir
