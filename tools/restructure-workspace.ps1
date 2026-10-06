# Restructures the workspace grid of MainWindow.xaml for the TIA style shell.
#
#   row 0 = breadcrumb bar, row 1 = editors, row 2 = splitter, row 3 = output
#   col 0 = project tree, col 2 = editors, col 4 = properties + instructions,
#   col 5 = vertical pane selector
#
# IMPORTANT: this script is deliberately ASCII only. Windows PowerShell 5.1 reads
# .ps1 files as ANSI, so non-ASCII literals here would arrive corrupted and end up
# written into the XAML. All Chinese text is copied verbatim from the source file
# instead of being re-typed.
$ErrorActionPreference = 'Stop'

$f = Join-Path $PSScriptRoot "..\src\TiaMc.App\Views\MainWindow.xaml"
$backup = Join-Path $env:TEMP 'MainWindow.before.xaml'
if (Test-Path $backup) {
    Copy-Item $backup $f -Force
    Write-Host "restored original MainWindow.xaml"
}

$lines = [System.IO.File]::ReadAllLines($f)
$out = New-Object System.Collections.Generic.List[string]

# --- 1. body (drop the old right column: splitter + property panel, 0-based 752..855)
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($i -ge 752 -and $i -le 855) { continue }
    $out.Add($lines[$i])
}

# --- 2. locate the property panel body and its caption text (copied verbatim)
$scrollStart = -1
$scrollEnd = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    $t = $lines[$i].Trim()
    if ($scrollStart -lt 0 -and $t.StartsWith('<ScrollViewer') -and $t.Contains('Padding="10,8,10,10"')) {
        $scrollStart = $i
    }
    elseif ($scrollStart -ge 0 -and $scrollEnd -lt 0 -and $t -eq '</ScrollViewer>') {
        $scrollEnd = $i
    }
}
if ($scrollStart -lt 0 -or $scrollEnd -lt 0) { throw "property body not found ($scrollStart/$scrollEnd)" }

# caption text of the property dock header, taken from the original file
$captionText = 'Properties'
for ($i = 740; $i -lt 760; $i++) {
    $t = $lines[$i].Trim()
    if ($t.StartsWith('<TextBlock Text="') -and $t.Contains('VerticalAlignment="Center"')) {
        $captionText = $t
        break
    }
}
Write-Host "property body lines $($scrollStart + 1)..$($scrollEnd + 1); caption: $captionText"

# --- 3. build the right column (ASCII markup only)
$r = New-Object System.Collections.Generic.List[string]
$r.Add('')
$r.Add('            <!-- right panes: properties (top) and instruction palette (bottom) -->')
$r.Add('            <Grid Grid.Row="0" Grid.RowSpan="4" Grid.Column="4">')
$r.Add('                <Grid.RowDefinitions>')
$r.Add('                    <RowDefinition Height="*" MinHeight="150" />')
$r.Add('                    <RowDefinition Height="4" />')
$r.Add('                    <RowDefinition Height="*" MinHeight="150" />')
$r.Add('                </Grid.RowDefinitions>')
$r.Add('')
$r.Add('                <Border Grid.Row="0" Background="{DynamicResource Tia.PanelBg}"')
$r.Add('                        BorderBrush="{DynamicResource Tia.Border}" BorderThickness="1,0,0,0"')
$r.Add('                        Visibility="{Binding ShowProperties, Converter={StaticResource BoolToVisibility}}">')
$r.Add('                    <DockPanel>')
$r.Add('                        <Border DockPanel.Dock="Top" Style="{StaticResource Tia.DockHeader}">')
$r.Add('                            ' + $captionText)
$r.Add('                        </Border>')
$r.Add('                        <ScrollViewer VerticalScrollBarVisibility="Auto" Padding="10,8,10,10">')
$r.AddRange([string[]]$lines[($scrollStart + 1)..($scrollEnd - 1)])
$r.Add('                        </ScrollViewer>')
$r.Add('                    </DockPanel>')
$r.Add('                </Border>')
$r.Add('')
$r.Add('                <Border Grid.Row="1" Background="{DynamicResource Tia.PanelBgAlt}"')
$r.Add('                        BorderBrush="{DynamicResource Tia.Border}" BorderThickness="0,1,0,1" />')
$r.Add('')
$r.Add('                <Border Grid.Row="2" Background="{DynamicResource Tia.PanelBg}"')
$r.Add('                        BorderBrush="{DynamicResource Tia.Border}" BorderThickness="1,0,0,0"')
$r.Add('                        Visibility="{Binding ShowPalette, Converter={StaticResource BoolToVisibility}}">')
$r.Add('                    <DockPanel>')
$r.Add('                        <Border DockPanel.Dock="Top" Style="{StaticResource Tia.DockHeader}">')
$r.Add('                            <TextBlock Text="Commands" VerticalAlignment="Center" />')
$r.Add('                        </Border>')
$r.Add('                        <ScrollViewer VerticalScrollBarVisibility="Auto">')
$r.Add('                            <ItemsControl ItemsSource="{Binding PaletteSections}">')
$r.Add('                                <ItemsControl.ItemTemplate>')
$r.Add('                                    <DataTemplate>')
$r.Add('                                        <StackPanel>')
$r.Add('                                            <ToggleButton Style="{StaticResource Tia.PaletteHeaderToggle}"')
$r.Add('                                                          Content="{Binding Title}"')
$r.Add('                                                          IsChecked="{Binding IsExpanded, Mode=TwoWay}" />')
$r.Add('                                            <ListBox ItemsSource="{Binding VisibleItems}"')
$r.Add('                                                     Style="{StaticResource Tia.ListBox}"')
$r.Add('                                                     ItemContainerStyle="{StaticResource Tia.PaletteRow}"')
$r.Add('                                                     BorderThickness="0" Background="Transparent"')
$r.Add('                                                     MouseDoubleClick="Palette_MouseDoubleClick"')
$r.Add('                                                     Visibility="{Binding IsExpanded, Converter={StaticResource BoolToVisibility}}">')
$r.Add('                                                <ListBox.ItemTemplate>')
$r.Add('                                                    <DataTemplate>')
$r.Add('                                                        <StackPanel Orientation="Horizontal">')
$r.Add('                                                            <Path Data="{Binding IconKey, Converter={StaticResource IconKeyToGeometry}}"')
$r.Add('                                                                  Fill="{DynamicResource Tia.Brand}" Width="13" Height="13"')
$r.Add('                                                                  Stretch="Uniform" VerticalAlignment="Center" />')
$r.Add('                                                            <TextBlock Text="{Binding Name}" Margin="5,0,0,0"')
$r.Add('                                                                       VerticalAlignment="Center" />')
$r.Add('                                                            <TextBlock Text="{Binding Description}" Margin="10,0,0,0"')
$r.Add('                                                                       VerticalAlignment="Center"')
$r.Add('                                                                       Style="{StaticResource Tia.PaletteGroupLabel}"')
$r.Add('                                                                       TextTrimming="CharacterEllipsis" />')
$r.Add('                                                        </StackPanel>')
$r.Add('                                                    </DataTemplate>')
$r.Add('                                                </ListBox.ItemTemplate>')
$r.Add('                                            </ListBox>')
$r.Add('                                        </StackPanel>')
$r.Add('                                    </DataTemplate>')
$r.Add('                                </ItemsControl.ItemTemplate>')
$r.Add('                            </ItemsControl>')
$r.Add('                        </ScrollViewer>')
$r.Add('                    </DockPanel>')
$r.Add('                </Border>')
$r.Add('            </Grid>')
$r.Add('')
$r.Add('            <Border Grid.Row="0" Grid.RowSpan="4" Grid.Column="5"')
$r.Add('                    Background="{DynamicResource Tia.PanelBgAlt}"')
$r.Add('                    BorderBrush="{DynamicResource Tia.Border}" BorderThickness="1,0,0,0">')
$r.Add('                <StackPanel Margin="0,6,0,0">')
$r.Add('                    <RadioButton Style="{StaticResource Tia.PaneTab}" GroupName="RightPane" Content="Properties"')
$r.Add('                                 IsChecked="{Binding RightPane, Converter={StaticResource IntEquals}, ConverterParameter=0}" />')
$r.Add('                    <RadioButton Style="{StaticResource Tia.PaneTab}" GroupName="RightPane" Content="Instructions"')
$r.Add('                                 IsChecked="{Binding RightPane, Converter={StaticResource IntEquals}, ConverterParameter=1}" />')
$r.Add('                </StackPanel>')
$r.Add('            </Border>')

# --- 4. insert before the workspace closing </Grid>
$closing = -1
for ($i = $out.Count - 1; $i -ge 0; $i--) {
    if ($out[$i].Trim() -eq '</Grid>' -and $out[$i - 1].Trim() -eq '</Border>') { $closing = $i; break }
}
if ($closing -lt 0) { throw 'workspace closing tag not found' }

$result = New-Object System.Collections.Generic.List[string]
$result.AddRange($out.GetRange(0, $closing))
$result.AddRange($r)
$result.AddRange($out.GetRange($closing, $out.Count - $closing))

[System.IO.File]::WriteAllLines($f, $result.ToArray(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "written: $($result.Count) lines (workspace grid closed at $($closing + 1))"
