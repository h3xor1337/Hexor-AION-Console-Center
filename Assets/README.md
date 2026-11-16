# Required official assets

Place the user-supplied official files here, preserving these exact names:

- Hexor.ico
- Hex Logo-Transparent.png

Neither file was present during the redesign. No substitute was generated or copied from another project. Hexor.ControlCenter.csproj conditionally embeds the supplied ICO as ApplicationIcon and both files as WPF resources when present. MainWindow uses those resources for Window.Icon/custom caption and sidebar branding.

Rebuild and rerun the visual/icon checks after supplying the files. A successful build while they are absent is not verification of official executable/taskbar/Alt+Tab branding.
