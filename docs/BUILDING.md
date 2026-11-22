# Building NodeLock

Requires Python 3.11+, .NET SDK 10, KSA 2026.11.16.5901 and StarMap 0.5.4. The StarMap directory must contain `StarMap.API.dll` and `0Harmony.dll`.

```powershell
python tools/build.py --game-dir "C:/path/to/KSA" --loader-dir "C:/path/to/StarMap"
python tools/package_release.py
```

The build runs selection, state and rotation tests and checks the native editor bindings. The package is written to `release/NodeLock-0.1.0.zip`, with its checksum in `release/SHA256SUMS.txt`.

To check the bundled XML patches against the game's content merger:

```powershell
dotnet run --project tools/VerifyContent -p:GameDir="C:/path/to/KSA" -- "C:/path/to/KSA" "C:/path/to/PurrlineTessera" content/TankBayCompat.xml
```

The runtime checks the supported KSA.dll hash before applying Harmony patches. Supporting another game build requires reviewing the editor bindings and updating this check. Game and loader binaries are not included in the repository or release.
