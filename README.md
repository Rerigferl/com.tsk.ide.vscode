# TSK VSCode Editor

[![Discord](https://img.shields.io/discord/1106106269837819914?color=D1495B&logo=discord&logoColor=FFFFFF&style=flat)](https://discord.gg/VU8EhUY7bX) [![openupm](https://img.shields.io/badge/dynamic/json?color=brightgreen&label=downloads&query=%24.downloads&suffix=%2Fmonth&url=https%3A%2F%2Fpackage.openupm.com%2Fdownloads%2Fpoint%2Flast-month%2Fcom.tsk.ide.vscode)](https://openupm.com/packages/com.tsk.ide.vscode/) [![openupm](https://img.shields.io/npm/v/com.tsk.ide.vscode?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.tsk.ide.vscode/)

Unity Code editor integration for VSCode. **(2021.3+)**

Check out the [Changelog](https://github.com/Chizaruu/com.tsk.ide.vscode/wiki/CHANGELOG) and [FAQs](https://github.com/Chizaruu/com.tsk.ide.vscode/wiki/FAQs) pages for more information.

**Note** - This package is [incompatible](https://github.com/Chizaruu/com.tsk.ide.vscode/issues/77) with **Unity Visual Studio** package.

If you find my package useful, please consider giving it a Star 🌟 to show your support. Thank you!

## Features

### Project SDK Support

This package offers comprehensive project SDK support based on .Net standards. By leveraging this support, you can utilize the latest C# features and language enhancements within your Unity projects, subject to Unity's compatibility.

### Organized .csproj Files

To enhance project structure and maintain cleanliness, the `com.tsk.ide.vscode` package facilitates the automatic separation of `.csproj` files into individual folders. These folders are consolidated within a main directory named "CSProjFolders." This organization ensures a more streamlined and organized project structure, contributing to improved clarity and ease of navigation.

### Successful Dotnet Build

The `com.tsk.ide.vscode` package ensures a seamless build process by guaranteeing successful execution of the `dotnet build` command. This means your project can be compiled and built without issues, ensuring a smooth development experience.

### Microsoft.Unity.Analyzers Integration [![NuGet](https://img.shields.io/nuget/v/Microsoft.Unity.Analyzers.svg)](https://nuget.org/packages/Microsoft.Unity.Analyzers)

In addition to its core features, this package includes seamless integration with Microsoft.Unity.Analyzers library. This integration provides access to various code analysis and validation tools designed for Unity projects. With the support of these analyzers, you can enhance code quality, identify potential issues, and adhere to best practices, ultimately improving the overall robustness and maintainability of your Unity projects.

### Streamlined Configuration Setup and Customization

The com.tsk.ide.vscode package presents a proficient solution designed for streamlined integration of Visual Studio Code with Unity. This package significantly simplifies the setup process by generating essential configuration files, namely `.editorconfig`. This conserves valuable time and boosts your efficiency by minimizing the potential for setup errors.

To utilize this feature, navigate to `Preferences > External Tools > Generate config files for:` and select the appropriate options to create the configuration files. After this, merely click on the `Regenerate` button.

Beyond simplifying setup, the com.tsk.ide.vscode package introduces a dedicated configuration section within External Tools. This component provides unprecedented control over the settings files generated, enabling manual customization in accordance with individual preferences and unique project requirements. This flexibility allows you to customize your development environment for the best possible productivity and outcome.

## Prerequisites
1. Install the .Net 7 SDK - <https://dotnet.microsoft.com/en-us/download>
2. Install the [C# Dev Kit extension](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) from the VS Code Marketplace.
3. Install the [Unity extension](https://marketplace.visualstudio.com/items?itemName=visualstudiotoolsforunity.vstuc) from the VS Code Marketplace

## Install via Package Manager

### Unity

-   Open Window/Package Manager
-   Click +
-   Select Add package from git URL
-   Paste `https://github.com/Chizaruu/com.tsk.ide.vscode.git#upm` into URL
-   Click Add

### OpenUPM

Please follow the instrustions:

-   Open Edit/Project Settings/Package Manager
-   Add a new Scoped Registry (or edit the existing OpenUPM entry)

```text
  Name: package.openupm.com
  URL: https://package.openupm.com
  Scope(s): com.tsk.ide.vscode
```

-   Click Save (or Apply)
-   Open Window/Package Manager
-   Click +
-   Select Add package by name... or Add package from git URL...
-   Paste com.tsk.ide.vscode into name
-   Paste 1.4.5 into version
-   Click Add

Alternatively, merge the snippet to Packages/manifest.json

```json
{
    "scopedRegistries": [
        {
            "name": "package.openupm.com",
            "url": "https://package.openupm.com",
            "scopes": ["com.tsk.ide.vscode"]
        }
    ],
    "dependencies": {
        "com.tsk.ide.vscode": "1.4.5"
    }
}
```

## Post Installation

### Regenerate .csprog files
After installing the package, follow these steps to regenerate the .csproj files:

1. Open the Preferences window.
2. Go to the External Tools tab.
3. Click on the Regenerate .csproj Files option.

The assembly project files will be auto-generated in {ProjectDirectory}/CSharpProjFolders.

To ignore these auto-generated files, add the following line to your .gitignore:

Example .gitignore lines:

```
# TSK VSCode
/CSharpProjFolders/*
```

### Extras

-   To enable grammar and highlighting for jslib files, install the [jslib-for-unity](https://github.com/TheSleepyKoala/jslib-for-unity) package.

## Contributing

Thank you for considering contributing to the `com.tsk.ide.vscode` package! To contribute, please follow these guidelines:

-   Create a new branch for your changes.
-   Discuss your changes by creating a new issue in the repository before starting work.
-   Follow the existing coding conventions and style.
-   Provide a clear description of your changes in your pull request.
-   Submit your pull request to the default branch.

We appreciate all contributions to com.tsk.ide.vscode!
