# FluxionSharp

This is the C# implementation of the Fluxion nodal data system. The code is always complete with the latest Fluxion
standard.

This library is written in C# native and features no extra libraries to work.

This library should be compatible with trimming and native ahead-of-time compiling.

This library has support for both v1 and v2 versions of Fluxion format.

This library (as for all Fluxion libraries) is released under GNU GPL v3 license. [Click here](./LICENSE) to see the
license.

Example documentation in the form of C# code is available in the FluxionSharp.Demo folder.

FluxionSharp is made with .NET Standard 2.0. It should be supported by these frameworks:

- .NET 5 and onwards
- .NET Core 2.0 and onwards
- .NET Framework 4.6.1 and onwards
- Mono 5.4 and onwards
- Xamarin.iOS 10.14 and onwards
- Xamarin.Mac 3.8 and onwards
- Xamarin.Android 8.0 and onwards
- Universal Windows Platform 10.0.16299 and onwards

## Building FluxionSharp

Requires .NET SDK (any version). The example project requires the latest supported versions of .NET to be as up-to-date
as possible.

1. Clone this repository by
   either [downloading as ZIP](https://github.com/Haltroy/FluxionSharp/archive/refs/heads/main.zip), using GitHub
   Desktop or
   using `git clone https://github.com/haltroy/FluxionSharp.git` command.
2. Open up a terminal in the folder of FluxionSharp or navigate a terminal with commands such as "cd".
3. Use `dotnet build` to build FluxionSharp. You can use IDEs such as Visual Studio, Vİsual Studio Code, Rider etc. to
   build it too.
4. The output files should be in the "bin" folder.
