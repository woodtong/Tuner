# -*- coding: utf-8 -*-
"""MASTER 并入应用通道滚动区，成为第一个通道；上方只留说明行"""
import io

p = 'src/Tuner.App/MainWindow.xaml'
src = io.open(p, encoding='utf-8').read()

# 定位：从顶部 Grid.Row=0 的说明+MASTER 块，到 应用通道 ScrollViewer —— 重排为单个调音台区
old_master = '''                    <Grid Grid.Row="0" Margin="0,0,4,10">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <StackPanel>
                            <TextBlock Text="输出与设备" FontSize="13.5" FontWeight="SemiBold"
                                       Foreground="{StaticResource TextPrimary}" Margin="6,2,0,0" />
                            <TextBlock x:Name="DeviceText2" Margin="6,6,0,0" Style="{StaticResource Hint}" />
                            <TextBlock Margin="6,10,0,0" MaxWidth="560" HorizontalAlignment="Left"
                                       Style="{StaticResource Hint}"
                                       Text="MASTER 作用于默认播放设备；右侧应用通道为各会话的独立音量。被闪避的应用由规则接管，此时调整将更新其恢复目标。" />
                        </StackPanel>
                        <!-- MASTER 通道（调音台主输出）：与应用通道同构的竖推子 -->
                        <Border Grid.Column="1" Width="96" Background="{StaticResource PageBg}"
                                BorderBrush="{StaticResource Accent}" BorderThickness="1"
                                CornerRadius="8" Padding="10,10">
                            <StackPanel>
                                <TextBlock Text="MASTER" FontSize="11" FontWeight="Bold" FontFamily="Consolas"
                                           Foreground="{StaticResource Accent}" HorizontalAlignment="Center" />
                                <controls:LedMeter Height="110" Width="14" Orientation="Vertical" Segments="16"
                                                   x:Name="MasterMeterBig" Margin="0,10,0,0"
                                                   HorizontalAlignment="Center" ToolTip="设备总峰值" />
                                <Slider x:Name="MasterSlider" Style="{StaticResource FaderV}" Height="130"
                                        Orientation="Vertical" Minimum="0" Maximum="100" Margin="0,10,0,0"
                                        HorizontalAlignment="Center" ValueChanged="OnMasterVolumeChanged" />
                                <TextBlock x:Name="MasterVolText" FontFamily="Consolas" FontSize="12"
                                           Foreground="{StaticResource TextSecondary}" Margin="0,6,0,0"
                                           HorizontalAlignment="Center" />
                                <Button x:Name="MasterMuteBtn" Content="静音" Width="66" Margin="0,8,0,0"
                                        Style="{StaticResource Btn}" Click="OnMasterMuteClick" />
                            </StackPanel>
                        </Border>
                    </Grid>'''
new_master = '''                    <TextBlock Grid.Row="0" FontSize="13.5" FontWeight="SemiBold"
                               Foreground="{StaticResource TextPrimary}" Margin="2,0,0,10"
                               Text="输出与设备：耳机 (HECATE GM400)" />'''
assert old_master in src, 'master top block'
src = src.replace(old_master, new_master)

# 应用通道块：标题行右侧说明合并；MASTER 卡插入 ScrollViewer 内 ItemsControl 之前
old_ch = '''                            <Grid Margin="0,0,0,12">
                                <TextBlock Text="应用通道" FontSize="13.5" FontWeight="SemiBold"
                                           Foreground="{StaticResource TextPrimary}" />
                                <TextBlock Text="实时电平与音量；被闪避的应用由规则接管，此时调整将更新其恢复目标"
                                           Style="{StaticResource Hint}" HorizontalAlignment="Right" />
                            </Grid>
                            <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
                                <ItemsControl x:Name="VolumeList">'''
new_ch = '''                            <Grid Margin="0,0,0,12">
                                <TextBlock Text="调音台 · MASTER + 应用通道" FontSize="13.5" FontWeight="SemiBold"
                                           Foreground="{StaticResource TextPrimary}" />
                                <TextBlock Text="MASTER=默认播放设备；应用通道实时电平与音量；被闪避时调整将更新恢复目标"
                                           Style="{StaticResource Hint}" HorizontalAlignment="Right" />
                            </Grid>
                            <ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Disabled">
                                <StackPanel Orientation="Horizontal">
                                <!-- MASTER 通道 -->
                                <Border Width="112" Background="{StaticResource PageBg}"
                                        BorderBrush="{StaticResource Accent}" BorderThickness="1"
                                        CornerRadius="8" Padding="10,10" Margin="0,0,10,4">
                                    <StackPanel>
                                        <TextBlock Text="MASTER" FontSize="11" FontWeight="Bold" FontFamily="Consolas"
                                                   Foreground="{StaticResource Accent}" HorizontalAlignment="Center"
                                                   Margin="0,2,0,8" />
                                        <controls:LedMeter Height="110" Width="14" Orientation="Vertical" Segments="16"
                                                           x:Name="MasterMeterBig" HorizontalAlignment="Center"
                                                           ToolTip="设备总峰值" />
                                        <Slider x:Name="MasterSlider" Style="{StaticResource FaderV}" Height="130"
                                                Orientation="Vertical" Minimum="0" Maximum="100" Margin="0,10,0,0"
                                                HorizontalAlignment="Center" ValueChanged="OnMasterVolumeChanged" />
                                        <TextBlock x:Name="MasterVolText" FontFamily="Consolas" FontSize="12"
                                                   Foreground="{StaticResource TextSecondary}" Margin="0,6,0,0"
                                                   HorizontalAlignment="Center" />
                                        <Button x:Name="MasterMuteBtn" Content="静音" Width="76" Margin="0,8,0,0"
                                                Style="{StaticResource Btn}" Click="OnMasterMuteClick" />
                                    </StackPanel>
                                </Border>
                                <ItemsControl x:Name="VolumeList">'''
assert old_ch in src, 'channels header'
src = src.replace(old_ch, new_ch)

# ItemsControl 结束后补 StackPanel 闭合
old_close = '''                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </ScrollViewer>'''
new_close = '''                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                                </ItemsControl>
                                </StackPanel>
                            </ScrollViewer>'''
assert old_close in src, 'channels close'
src = src.replace(old_close, new_close)

# 设备名文本绑定回填（DeviceText2 已无；改绑到标题 TextBlock）
old_dev = '''        DeviceText.Text = App.Monitor.DeviceName;
        DeviceText2.Text = App.Monitor.DeviceName;'''
new_dev = '''        DeviceText.Text = App.Monitor.DeviceName;
        OutputTitle.Text = "输出与设备：" + App.Monitor.DeviceName;'''
assert old_dev in src, 'devbind'
src = src.replace(old_dev, new_dev)
old_title = 'Text="输出与设备：耳机 (HECATE GM400)" />'
new_title = 'x:Name="OutputTitle" Text="输出与设备" />'
assert old_title in src, 'title name'
src = src.replace(old_title, new_title)

io.open(p, 'w', encoding='utf-8', newline='').write(src)
print('mixer strip done')
