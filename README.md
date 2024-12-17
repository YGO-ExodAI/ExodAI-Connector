# WindBot Ignite

A fork of [WindBot](https://github.com/ProjectIgnis/windbot/), modify for use as an entry point to the EDOPro client for AI models

### Usage

To use the bot in its current form, clone the repo (if you run into any issues with dependencies during compilation see the link below) and
launch Main from Program.cs in the Windbot project. You must have an open lobby of the EDOPro client with one empty player slot or the
bot will fail to find a room and quit out. From there you can see the options the player has at a given moment via the console.

### libWindbot

To actually compile libWindbot including the post-build task that produces the
Android aar artifact, you will need the following EXACT setup. You _will_ have a
bad day otherwise and this has been kept concise.

- The postbuild event runs on Windows only.
- You must use Visual Studio 2017 or Visual Studio 2019.
- You need Visual Studio workloads for Android (Xamarin and native development).
- You must install the 32-bit Mono SDK. The 64-bit version does not work.
- In the Visual Studio 2017 `Tools > Options > Xamarin > Android Settings`,
  ensure the SDK, NDK, and JDK all point to valid paths. They should be set
  correctly by default. You can use Microsoft-provided installations or share
  these with Android Studio.
  - In addition to the default Android SDK tools, install Platform 24
    (Android 7.0). No newer platform works.
  - The NDK path must point to an r15c installation. Visual Studio 2017 should
    already have installed it somewhere but you can download this unsupported
    old version from the Android developer site. No newer NDK works.

These are all quirks of the 0.4.0 NuGet version of
[Embeddinator-4000](https://github.com/mono/Embeddinator-4000), used to
transform the .NET DLL into a native library for Android.

## License

WindBot Ignite is free/libree and open source software licensed under the GNU
Affero General Public License, version 3 or later. Please see
[LICENSE](https://github.com/ProjectIgnis/windbot/blob/master/LICENSE) and
[COPYING](https://github.com/ProjectIgnis/windbot/blob/master/COPYING) for more
details.
