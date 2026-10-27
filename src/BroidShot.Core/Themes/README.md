# BroidShot tema (Theme.xaml)

Her zaman koyu, tek vurgu rengi BroidShot kırmızısı. Uygulama bu sözlüğü `Application.Resources`'a ekler; modül sayfaları ayrıca bir şey yapmadan alır. Bağımsız çalıştırılan doğrulama programları `ThemeResources.Load()` ile ekleyebilir.

## Kurallar (bunlar bozulunca görsel hata çıkar)

- **Yazı tipi Inter 4.1** (uygulamaya gömülü, SIL OFL 1.1 — `Fonts/OFL.txt`, çıktıda `licenses/Inter-OFL.txt`). Regular/Medium/SemiBold/Bold statik TTF; değişken font kullanılmaz (WPF düzgün çizmez). Kaynak adı `UiFont` (eski adlar `AppFont`, `HeadingFont` aynı fontu verir); kodda `ThemeResources.UiFontFamily`. Segoe Fluent Icons / MDL2 yalnız ikonlar içindir.
- **Netlik:** pencere kökünde `TextFormattingMode=Display`, `ClearType`, `UseLayoutRounding`, `SnapsToDevicePixels`. 20 px ve üstü başlıklar (`PageHeading`, `SubtitleText`) `Ideal` kullanır.
- **Örtük `TextBlock` stili yazı tipi/boyutu vermez.** Yazı tipi pencereden miras gelir. `ThemeResources.ApplyDefaults()` (uygulama başında çağrılır) modüllerin açtığı pencerelere de Inter 14 px verir. Önceki temada bu stil `FontFamily` ayarladığı için `IconButton` içindeki glif (`Content="&#xE711;"`) Segoe UI ile çizilip boş kutu oluyordu.
- **Odak çerçevesi yalnız klavyeyle gelir** (`FocusVisualStyle`). `IsKeyboardFocused` tetikleyicisiyle kırmızı çerçeve çizmeyin; fare tıklamasından sonra çerçeve takılı kalır.
- **Renkleri sabit yazmayın** (`#111111` gibi). Aşağıdaki fırçaları `{StaticResource …}` ile kullanın.
- İkonlar Segoe Fluent Icons / MDL2 glifleridir: `{StaticResource IconFont}` veya `IconText` stili. Kabuk, ikon yazı tipinde olmayan modül glifini nötr bir glifle değiştirir.
- Sayfa dolgusu ve kaydırma kabuktan gelir: sayfa kendi dış `Margin`/`Padding`'ini vermesin. Kökü düz `StackPanel`/`WrapPanel` olan sayfa (veya içeriği öyle olan `UserControl`) otomatik olarak `ScrollViewer` içine alınır. Kendi yerleşimini yöneten sayfa (`Grid`, `DockPanel`, kendi `ScrollViewer`'ı) tüm alanı doldurur.

## Renkler

Her renk hem `…Color` (`Color`) hem `…Brush` (`SolidColorBrush`) olarak vardır.

| Fırça | Kullanım |
| --- | --- |
| `RailBrush` | Kenar çubuğu, üst çubuk, başlık çubuğu |
| `WindowBackgroundBrush` | İçerik zemini |
| `SurfaceBrush` / `SurfaceHoverBrush` / `SurfaceActiveBrush` | Kart, üzerine gelince, seçili/basılı |
| `ControlBrush` / `ControlHoverBrush` | Metin kutusu, açılır liste zemini, iç panel |
| `OverlayBrush` | Açılır pencere, menü, palet, bildirim |
| `StrokeSubtleBrush` / `StrokeBrush` / `StrokeStrongBrush` | Ayırıcı / kontrol kenarı / üzerine gelince kenar |
| `TextBrush` / `TextSecondaryBrush` / `TextTertiaryBrush` / `TextDisabledBrush` | Metin seviyeleri |
| `AccentBrush` (+`Hover`, `Pressed`) | Birincil eylem, seçili gösterge, odak |
| `AccentSubtleBrush` / `AccentMutedBrush` / `AccentTextBrush` / `OnAccentBrush` | Kırmızı tonlu zemin / kenar / koyu zeminde kırmızı metin / kırmızı üstünde beyaz |
| `SuccessBrush`, `WarningBrush`, `DangerBrush`, `InfoBrush` (+ `…SubtleBrush`) | Durum renkleri |

Eski adlar çalışmaya devam eder: `CanvasBrush`, `PanelBrush`, `RaisedBrush`, `MutedBrush`, `SecondaryTextBrush`, `DividerBrush`, `SelectedBrush`, `AccentSoftBrush`, `SidebarBrush`. Yeni kodda yukarıdaki adları kullanın.

## Yazı, şekil, boşluk

- Yazı tipleri: `UiFont` (Inter), `MonoFont`, `IconFont`.
- Ölçek 12 / 13 / 14 / 16 / 20 / 28; ağırlıklar Normal (400), Medium (500), SemiBold (600). Başka boyut kullanmayın.
- Boyutlar (`sys:Double`): `FontSizeCaption` 12, `FontSizeSmall` 13, `FontSizeBody` 14, `FontSizeBodyLarge` 16, `FontSizeSubtitle` 20, `FontSizeTitle` 28, `IconSizeSmall` 12, `IconSize` 16, `IconSizeLarge` 20, `ControlHeight` 34.
- Köşe (`CornerRadius`): `RadiusSmall` 4, `RadiusControl` 6, `RadiusCard` 10, `RadiusOverlay` 10, `RadiusPill`.
- Boşluk (`Thickness`): `PagePadding` (kabuk uygular), `CardPadding` 20,18, `ControlPadding`, `SectionMargin` (bölümler arası 28), `StackGap` (alt 12), `InlineGap` (sağ 8).

## Örtük stiller (anahtar vermeden hazır)

`Window`, `ToolTip`, `Button` (ikincil görünüm), `ToggleButton`, `TextBox`, `PasswordBox`, `CheckBox`, `RadioButton`, `ComboBox`/`ComboBoxItem` (düzenlenebilir mod dahil), `ListBox`/`ListBoxItem` (yuvarlatılmış satır, seçilince kırmızı çubuk), `ScrollViewer`/`ScrollBar` (ince, gri köşe karesi yok), `ContextMenu`/`MenuItem`/`Separator` (TextBox'ın sağ tık menüsü dahil koyu), `ProgressBar` (belirsiz modda nabız), `Hyperlink`.

## Ekli özellikler (`core:ThemeAssist`)

`xmlns:core="clr-namespace:BroidShot.Core;assembly=BroidShot.Core"`

- `core:ThemeAssist.Icon="&#xE8C8;"` — herhangi bir Button/ToggleButton'da metnin önüne glif çizer. `Content` boşsa yalnız glif görünür.
- `core:ThemeAssist.Placeholder="Ara…"` — TextBox boşken ipucu metni (`TextBox`, `SearchBox`, `BareTextBox` stillerinde).
- `core:ThemeAssist.CornerRadius="8"` — düğme, girdi ve liste öğesi köşesini değiştirir.

## Adlandırılmış stiller

**Düğmeler**

| Anahtar | Ne zaman |
| --- | --- |
| `PrimaryButton` (eski adı `AccentButton`) | Sayfanın/iletişim kutusunun ana eylemi (kırmızı dolgu). Bir alanda bir tane. |
| `SecondaryButton` (= varsayılan) | Diğer eylemler |
| `SubtleButton` | Saydam + kenarlı; kart içi ikincil eylem, filtre |
| `GhostButton` | Kenarsız, üzerine gelince belirir |
| `ToolbarButton` | Araç çubuğu düğmesi; `ThemeAssist.Icon` ile kullanın |
| `DangerButton` | Silme gibi geri alınamaz eylem |
| `LinkButton` | Metin bağlantısı gibi |
| `IconButton` | Yalnız glif (32×32). `ToolTip` ve `AutomationProperties.Name` verin. |
| `TileButton` | Büyük tıklanabilir kart (ikon + başlık + açıklama) |
| `NavButton` | Sayfa içi sol liste öğesi, `Tag="selected"` seçili |
| `FilterChip` (ToggleButton) | Hap biçimli filtre; `IsChecked` seçili |

**Metin:** `PageHeading`, `PageSubtitle`, `SubtitleText` (20), `SectionHeading` (16, kart/bölüm başlığı), `SectionLabel` (12, küçük grup etiketi), `BodyText`, `BodyStrongText`, `MutedText`, `SmallLabel`, `CaptionText`, `AccentText`, `MonoText`, `IconText`.

**Girdi:** `SearchBox` (büyüteçli), `CodeTextBox` (eş aralıklı, çok satır), `BareTextBox` (kenarsız), `ToggleSwitch` (CheckBox'ı açma/kapama anahtarına çevirir).

**Kapsayıcılar (Border):** `Card`, `SettingsCard` + `SettingRow` (ayar satırları: solda başlık/açıklama, sağda kontrol), `InsetPanel` (kart içi koyu alan), `Toolbar`, `Divider`, `VerticalDivider`, `IconTile` / `AccentIconTile` (36 px ikon karesi), `Chip`, `AccentChip`, `SuccessChip`, `WarningChip`, `DangerChip`, `Keycap` (kısayol gösterimi), `InfoBar`, `WarningBar`, `ErrorBar`.

**Liste:** `CardListItem` (her öğe ayrı kart; `ItemContainerStyle` olarak).

**Boş durum:** `EmptyStatePanel` (StackPanel) > `EmptyStateIcon` (Border, içine IconText) + `EmptyStateTitle` + `EmptyStateText` (+ isteğe bağlı `PrimaryButton`).

## Örnek

```xml
<StackPanel MaxWidth="780" HorizontalAlignment="Left">
  <Border Style="{StaticResource Card}" Margin="{StaticResource SectionMargin}">
    <StackPanel>
      <TextBlock Text="Çıktı" Style="{StaticResource SectionHeading}" />
      <TextBox core:ThemeAssist.Placeholder="Dosya adı" Margin="{StaticResource StackGap}" />
      <CheckBox Content="Bitince klasörü aç" />
    </StackPanel>
  </Border>
  <WrapPanel>
    <Button Content="Dönüştür" Style="{StaticResource PrimaryButton}" core:ThemeAssist.Icon="&#xE8AB;" />
    <Button Content="Temizle" Style="{StaticResource SubtleButton}" />
  </WrapPanel>
</StackPanel>
```

## Modül ikonları için öneriler

`IToolModule.Icon` tek bir Segoe Fluent/MDL2 glifidir. Kabuk şu iki eski glifi şimdilik düzeltir; modül kendi ikonunu değiştirince düzeltme devreden çıkar: Cetvel `E7C9` → `ED5E` (Ruler), QR `E8EF` (Calculator) → `ED14` (QRCode).
