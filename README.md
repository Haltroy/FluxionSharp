# FluxionSharp

This is the C# implementation of the Fluxion nodal data system. The code is always complete with the latest Fluxion
standard.

This library is written in C# native and features no extra libraries to work.

This library should be compatible with trimming and native ahead-of-time compiling.

This library has support for all versions of Fluxion format as well as the next version under the
[flx-next](https://github.com/Haltroy/FluxionSharp/tree/flx-next) branch.

Please note that the `flx-next` branch is experimental. It is not recommended to be used in a production environment
as this in-development branch might change ciritical stuff.

This library (as for all Fluxion libraries) is released under GNU GPL v3 license. [Click here](./LICENSE) to see the
license.

Example documentation in the form of C# code is available in the FluxionSharp.Demo folder.

FluxionSharp is made with .NET Standard 2.0. It should be supported by these frameworks:

- .NET Core 2.0, .NEt 5 and onwards
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


## Testing

You can test this library by either running the Demo application to see how to use FluxionSharp or use the test app
to benchmark Fluxion versions awith each other and with other formats as well.

There's no need for command-line options for Demo app. 

FluxionTest should output a Markdown table if successful.

Command-line options for FluxionText (`dotnet run -- [options]`):
 - `--sample-size <number>`: Determines how much nodes should be added to each format and version. Default is 10000.
 - `--count <number>`: Determines the number of runs for each format. Default is 5.
 - `--no-progress`: Removes the progress dialog.
 - `--disable-xml`: Disables XML format.
 - `--disable-flx <number, divided by a comma (",")>`: Disables the Fluxion versions mentioned. 
   - If no number is specified, all Fluxion tests will be disabled.
 - `--test-string <string>`: Specifies the test string. If the string is "random", it will be randomized for all nodes.
 - `--use-attributes`: Tests attributes as well.
