**BroidShot visual identity**

Original vector mark: geometric B inside a screenshot frame. The application and tile use near-black; neutral grey frame corners keep the symbol legible, and a single fine red corner gives a restrained capture accent. The mark contains no gradients or glow.

- Background: `#09090B`
- Monogram: `#F1EFED`
- Secondary frame: `#79797F`
- Accent: `#E54040`

`Logo.xaml` is the WPF source (`BroidShotLogo` DrawingImage). `BroidShot-logo.svg` matches its geometry. `BroidShot-logo.png` is a 512px render. `BroidShot.ico` embeds PNG frames at 16, 20, 24, 32, 40, 48, 64, 128 and 256px. The wordmark SVG uses the system's Segoe UI/Inter/Arial font stack; the standalone mark has no font dependency.

Run `Generate-Assets.ps1` to regenerate. The source generator is kept as `.txt` templates here so the application does not compile the tool; the script creates and runs its WPF project in `artifacts/branding/tools`. It reads the WPF dictionary so the Windows icon and preview stay aligned. Do not manually recolour just one output file.

Integration: merge `Branding/Logo.xaml` into application resources; use `{StaticResource BroidShotLogo}` for Image.Source, set `<ApplicationIcon>Branding/BroidShot.ico</ApplicationIcon>` in the project, set Window.Icon to `/Branding/BroidShot.ico` (Resource), and load the same `.ico` from an embedded resource or copied file for the tray. Keep at least a quarter-symbol width of space around the mark in layouts.
