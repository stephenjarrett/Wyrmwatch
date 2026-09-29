# Third-party notices

Wyrmwatch's original project material is licensed under AGPL-3.0-only. The components listed here retain their separate licenses. Their copyright notices and full license texts are included in [licenses/third-party](licenses/third-party). Files in that directory are reproduced from their upstream sources or the installed NuGet packages.

| Component | Version in this build | License / included notices |
| --- | --- | --- |
| Avalonia and its Desktop, platform, Fluent theme, font wrapper, rendering, and protocol packages | 12.1.3 | [MIT](licenses/third-party/Avalonia-MIT.txt) |
| ANGLE Windows native libraries | 2.1.27548.20260419 | [Upstream license](licenses/third-party/ANGLE-LICENSE.txt) |
| Inter font files | Bundled by Avalonia.Fonts.Inter 12.1.3 | [SIL Open Font License 1.1](licenses/third-party/Inter-OFL.txt) |
| MicroCom.Runtime | 0.11.6 | [MIT](licenses/third-party/MicroCom-MIT.txt) |
| Tmds.DBus.Protocol | 0.94.1 | [MIT](licenses/third-party/Tmds.DBus-MIT.txt) |
| SkiaSharp and native assets | 3.119.4 | [MIT](licenses/third-party/SkiaSharp-LICENSE.txt), [third-party notices](licenses/third-party/SkiaSharp-NOTICES.txt) |
| HarfBuzzSharp and native assets | 8.3.1.3 | [MIT](licenses/third-party/HarfBuzzSharp-LICENSE.txt), [third-party notices](licenses/third-party/HarfBuzzSharp-NOTICES.txt) |
| .NET runtime in self-contained builds | 10.0.x; resolved by the publishing SDK | [MIT](licenses/third-party/dotnet-LICENSE.txt), [third-party notices](licenses/third-party/dotnet-NOTICES.txt) |

Avalonia package metadata also records: Copyright 2013-2026 © The AvaloniaUI Project. MicroCom package metadata records: Copyright 2021 © Nikita Tsukanov. Tmds.DBus package metadata records: Tom Deseyn. The original notices are retained in the linked license files.

## Sources

- Avalonia: [license at the package's source commit](https://github.com/AvaloniaUI/Avalonia/blob/8eeda4f6f546165b3f72e63c9f42247abb306905/licence.md).
- Inter: [official font license](https://github.com/rsms/inter/blob/master/LICENSE.txt). The font's own OFL terms are separate from the Avalonia wrapper's MIT terms.
- MicroCom: [license at the package's source commit](https://github.com/kekekeks/MicroCom/blob/76785efcafd91b5902fd19dd11145f6dd655b7b4/LICENSE).
- Tmds.DBus: [license at the package's source commit](https://github.com/tmds/Tmds.DBus/blob/b4a7fed0b878f74cb54f7cca84d2889af4e596ba/COPYING).
- ANGLE: `LICENSE` from the `Avalonia.Angle.Windows.Natives` NuGet package listed above.
- SkiaSharp / HarfBuzzSharp: `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` from the respective managed and native-asset NuGet packages. The Windows and Linux native packages supply identical notices for these versions.
- .NET: `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` from the .NET 10.0.12 runtime packages. The packaging script includes the publishing runtime's own notices alongside these reference copies when present.

Only the native components for the selected platform are included in a portable build. NuGet restore metadata in each project's `obj/project.assets.json` lists the resolved dependency versions. Development/test tools are not part of the portable application and retain their own package licenses.

SteamCMD and the Dragonwilds server are downloaded separately from their providers when explicitly requested. They are not bundled in Wyrmwatch and remain subject to their providers' terms. RuneScape and Dragonwilds names belong to their respective owners; this community project does not imply endorsement.
