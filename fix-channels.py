# -*- coding: utf-8 -*-
"""音量页通道整改：删徽章、固定段高对齐、加大推子命中区"""
import io

p = 'src/Tuner.App/MainWindow.xaml'
src = io.open(p, encoding='utf-8').read()

# ---------- 1) 通道模板：删徽章；名称/表/推子/百分比/静音全部固定高度，保证跨通道对齐 ----------
old = '''                                            <DataTemplate>
                                                <Border Width="112" Background="{StaticResource PageBg}"
                                                        BorderBrush="{StaticResource CardBorder}" BorderThickness="1"
                                                        CornerRadius="8" Padding="10,10" Margin="0,0,10,4">
                                                    <StackPanel>
                                                        <Border Background="{Binding GroupBrush}" CornerRadius="4" Padding="6,2"
                                                                HorizontalAlignment="Center">
                                                            <TextBlock Text="{Binding GroupName}" FontSize="10.5" Foreground="White" />
                                                        </Border>
                                                        <TextBlock Text="{Binding Name}" FontSize="12" FontWeight="SemiBold"
                                                                   Foreground="{StaticResource TextPrimary}" Margin="0,8,0,0"
                                                                   TextAlignment="Center" TextWrapping="Wrap" MaxHeight="32"
                                                                   TextTrimming="CharacterEllipsis" />
                                                        <TextBlock Text="{Binding PidText}" FontSize="9.5" FontFamily="Consolas"
                                                                   Foreground="{StaticResource TextFaint}" Margin="0,1,0,0"
                                                                   TextAlignment="Center" />
                                                        <controls:LedMeter Height="110" Width="14" Orientation="Vertical"
                                                                           Segments="16" Value="{Binding PeakPercent}"
                                                                           Margin="0,10,0,0" HorizontalAlignment="Center" />
                                                        <Slider Style="{StaticResource FaderV}" Height="130" Orientation="Vertical"
                                                                Minimum="0" Maximum="100" Value="{Binding VolumePercent}"
                                                                ValueChanged="OnChannelVolumeChanged" Margin="0,10,0,0"
                                                                HorizontalAlignment="Center" />
                                                        <TextBlock Text="{Binding VolumeText}" FontFamily="Consolas" FontSize="12"
                                                                   Foreground="{StaticResource TextSecondary}" Margin="0,6,0,0"
                                                                   TextAlignment="Center" />
                                                        <Button Content="{Binding MuteText}" Width="76" Margin="0,8,0,0"
                                                                Style="{StaticResource Btn}" Click="OnChannelMuteClick" />
                                                    </StackPanel>
                                                </Border>
                                            </DataTemplate>'''
new = '''                                            <DataTemplate>
                                                <Border Width="112" Background="{StaticResource PageBg}"
                                                        BorderBrush="{StaticResource CardBorder}" BorderThickness="1"
                                                        CornerRadius="8" Padding="10,10" Margin="0,0,10,4">
                                                    <StackPanel>
                                                        <!-- 固定高度名称区（两行），跨通道对齐 -->
                                                        <TextBlock Text="{Binding Name}" FontSize="12" FontWeight="SemiBold"
                                                                   Height="32" Foreground="{StaticResource TextPrimary}"
                                                                   TextAlignment="Center" TextWrapping="Wrap"
                                                                   TextTrimming="CharacterEllipsis" />
                                                        <controls:LedMeter Height="110" Width="14" Orientation="Vertical"
                                                                           Segments="16" Value="{Binding PeakPercent}"
                                                                           HorizontalAlignment="Center" />
                                                        <Slider Style="{StaticResource FaderV}" Height="130" Orientation="Vertical"
                                                                Minimum="0" Maximum="100" Value="{Binding VolumePercent}"
                                                                ValueChanged="OnChannelVolumeChanged" Margin="0,8,0,0"
                                                                HorizontalAlignment="Center" />
                                                        <TextBlock Text="{Binding VolumeText}" FontFamily="Consolas" FontSize="12"
                                                                   Height="18" Foreground="{StaticResource TextSecondary}"
                                                                   Margin="0,6,0,0" TextAlignment="Center" />
                                                        <Button Content="{Binding MuteText}" Width="76" Margin="0,8,0,0"
                                                                Style="{StaticResource Btn}" Click="OnChannelMuteClick" />
                                                    </StackPanel>
                                                </Border>
                                            </DataTemplate>'''
assert old in src, 'channel template'
src = src.replace(old, new)

# MASTER 通道同步对齐（名称区固定 32 高）
old = '''                                    <StackPanel>
                                        <TextBlock Text="MASTER" FontSize="11" FontWeight="Bold" FontFamily="Consolas"
                                                   Foreground="{StaticResource Accent}" HorizontalAlignment="Center"
                                                   Margin="0,2,0,8" />'''
new = '''                                    <StackPanel>
                                        <TextBlock Text="MASTER" FontSize="11" FontWeight="Bold" FontFamily="Consolas"
                                                   Foreground="{StaticResource Accent}" HorizontalAlignment="Center"
                                                   Height="32" Margin="0,0,0,0" />'''
assert old in src, 'master label'
src = src.replace(old, new)

io.open(p, 'w', encoding='utf-8', newline='').write(src)
print('xaml done')

# ---------- 2) FaderV 命中区：Thumb 加大透明命中层 ----------
p2 = 'src/Tuner.App/Theme.xaml'
src2 = io.open(p2, encoding='utf-8').read()
old2 = '''                            <Track.Thumb>
                                <Thumb Width="20" Height="10" Cursor="Hand">
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Border Background="{StaticResource TextPrimary}" CornerRadius="3" />
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>'''
new2 = '''                            <Track.Thumb>
                                <Thumb Width="30" Height="26" Cursor="Hand">
                                    <!-- 命中区大于可见推帽，解决"总是选不中" -->
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Grid Background="Transparent">
                                                <Border Width="20" Height="10" Background="{StaticResource TextPrimary}"
                                                        CornerRadius="3" HorizontalAlignment="Center"
                                                        VerticalAlignment="Center" />
                                            </Grid>
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

</ResourceDictionary>'''
assert old2 in src2, 'fader thumb'
src2 = src2.replace(old2, new2)

# 横向推子同步加大命中区
old3 = '''                            <Track.Thumb>
                                <Thumb Width="12" Height="16" Cursor="Hand">
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Border Background="{StaticResource TextPrimary}" CornerRadius="3" />
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>'''
new3 = '''                            <Track.Thumb>
                                <Thumb Width="24" Height="26" Cursor="Hand">
                                    <Thumb.Template>
                                        <ControlTemplate TargetType="Thumb">
                                            <Grid Background="Transparent">
                                                <Border Width="12" Height="16" Background="{StaticResource TextPrimary}"
                                                        CornerRadius="3" HorizontalAlignment="Center"
                                                        VerticalAlignment="Center" />
                                            </Grid>
                                        </ControlTemplate>
                                    </Thumb.Template>
                                </Thumb>
                            </Track.Thumb>'''
assert old3 in src2, 'h fader thumb'
src2 = src2.replace(old3, new3)

io.open(p2, 'w', encoding='utf-8', newline='').write(src2)
print('fader hit areas done')
