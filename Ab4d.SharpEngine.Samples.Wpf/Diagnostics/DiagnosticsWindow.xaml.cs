﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Xml;
using Ab4d.SharpEngine;
using Ab4d.SharpEngine.Cameras;
using Ab4d.SharpEngine.Common;
using Ab4d.SharpEngine.Effects;
using Ab4d.SharpEngine.Core;
using Ab4d.SharpEngine.Utilities;
using Ab4d.SharpEngine.Vulkan;
using Ab4d.SharpEngine.Wpf;
using Ab4d.Vulkan;

namespace Ab4d.SharpEngine.Samples.Wpf.Diagnostics
{
    /// <summary>
    /// Interaction logic for DiagnosticsWindow.xaml
    /// </summary>
    public partial class DiagnosticsWindow : Window
    {
        private ISharpEngineSceneView? _sharpEngineSceneView;

        public ISharpEngineSceneView? SharpEngineSceneView
        {
            get { return _sharpEngineSceneView; }
            set
            {
                if (ReferenceEquals(_sharpEngineSceneView, value))
                    return;

                ClearLogMessages(); // Clear log messages and warnings from previous DXView

                RegisterSceneView(value);
                _sharpEngineSceneView = value;
            }
        }

        public const double InitialWindowWidth = 310;

        public bool IsSharpEngineDebugBuild { get; private set; }

        public bool ShowProcessCpuUsage { get; set; }

        public string DumpFileName { get; set; }

        private static readonly int UpdateStatisticsInterval = 100; // 100 ms = update statistics 10 times per second

        private bool _isManuallyEnabledCollectingStatistics;

        private DateTime _lastStatisticsUpdate;
        private DateTime _lastPerfCountersReadTime;

        private bool _isGpuDeviceCreatedSubscribed;

        private bool _showRenderingStatistics = true;

        private Queue<double>? _fpsQueue;

        private List<Tuple<LogLevels, string>>? _logMessages;
        private int _deletedLogMessagesCount;
        private const int MaxLogMessages = 200;

        private string? _logMessagesString;

        private LogMessagesWindow? _logMessagesWindow;

        private bool _isOnSceneRenderedSubscribed;

        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _processorFrequencyCounter;

        private float _lastCpuUsage;

        private int _processorsCount = 1;
        private DispatcherTimer? _updateStatisticsTimer;

        private StringBuilder? _renderingStatisticStringBuilder;
        private RenderingStatistics? _lastRenderingStatisticsWithRecorderCommandBuffers;

        private SceneDirtyFlags _lastSceneDirtyFlags;
        private SceneViewDirtyFlags _lastSceneViewDirtyFlags;

        private WpfBitmapIO _wpfBitmapIO;

        public DiagnosticsWindow(ISharpEngineSceneView sharpEngineSceneView)
            : this()
        {
            this.SharpEngineSceneView = sharpEngineSceneView;
        }


        public DiagnosticsWindow()
        {
            InitializeComponent();

            ShowProcessCpuUsage = false;

            this.Width = InitialWindowWidth;


            string dumpFolder;
            if (System.IO.Directory.Exists(@"C:\temp"))
                dumpFolder = @"C:\temp\";
            else
                dumpFolder = System.IO.Path.GetTempPath();

            DumpFileName = System.IO.Path.Combine(dumpFolder, "SharpEngineDump.txt");


            // Set SharpEngine assembly version
            var version = typeof(VulkanDevice).Assembly.GetName().Version ?? new Version(0, 0);

            // IsDebugVersion field is defined only in Debug version
            var fieldInfo = typeof(VulkanDevice).GetField("IsDebugVersion");
            IsSharpEngineDebugBuild = fieldInfo != null;

            SharpEngineInfoTextBlock.Text = string.Format("Ab4d.SharpEngine v{0}.{1}.{2}{3}",
                version.Major, version.Minor, version.Build,
                IsSharpEngineDebugBuild ? " (debug build)" : "");

            Log.AddLogListener(OnLogAction);

            // When the window is shown start showing statistics
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(StartShowingStatistics));

            _wpfBitmapIO = new WpfBitmapIO();

            this.Loaded += OnLoaded;
            this.Closing += OnClosing;
        }

        private void OnLoaded(object sender, RoutedEventArgs routedEventArgs)
        {
            UpdateEnabledMenuItems();
        }

        private void UpdateEnabledMenuItems()
        {
            // On each new scene reset the StartStopCameraRotationMenuItem text
            //StartStopCameraRotationMenuItem.Header = "Toggle camera rotation";


            //if (_dxView != null && (_dxView.MasterDXView != null || _dxView.ChildDXViews != null)) // ChildDXViews is set to null when the last child is disconnected (so list Count is never 0)
            //{
            //    ChangeDXViewSeparator.Visibility = Visibility.Visible;
            //    ChangeDXViewMenuItem.Visibility  = Visibility.Visible;
            //}
            //else
            //{
            //    ChangeDXViewSeparator.Visibility = Visibility.Collapsed;
            //    ChangeDXViewMenuItem.Visibility  = Visibility.Collapsed;
            //}
        }

        private void OnLogAction(LogLevels logLevel, string message)
        {
            _logMessagesString += logLevel.ToString() + ": " + message + Environment.NewLine;


            _logMessages ??= new List<Tuple<LogLevels, string>>();

            if (_deletedLogMessagesCount > _logMessages.Count)
                _deletedLogMessagesCount = 0; // This means that the messages were deleted in LogMessagesWindow

            if (_logMessages.Count >= MaxLogMessages)
            {
                // remove first 1/10 of messages
                int logMessagesToDelete = (int) (MaxLogMessages/10); 
                _logMessages.RemoveRange(0, logMessagesToDelete);

                _deletedLogMessagesCount += logMessagesToDelete;
            }

            _logMessages.Add(new Tuple<LogLevels, string>(logLevel, message));

            var numberOfWarnings = _logMessages.Count(t => t.Item1 >= LogLevels.Warn);
            if (numberOfWarnings > 0)
            {
                WarningsCountTextBlock.Text = (numberOfWarnings + _deletedLogMessagesCount).ToString();
                LogWarningsPanel.Visibility = Visibility.Visible;
            }

            if (_logMessagesWindow != null)
            {
                _logMessagesWindow.MessageStartIndex = _deletedLogMessagesCount + 1;
                _logMessagesWindow.UpdateLogMessages();
            }
        }

        private void ClearLogMessages()
        {
            if (_logMessages != null)
                _logMessages.Clear();
            
            _deletedLogMessagesCount = 0;

            if (_logMessagesWindow != null)
            {
                _logMessagesWindow.MessageStartIndex = 0;
                _logMessagesWindow.UpdateLogMessages();
            }

            LogWarningsPanel.Visibility = Visibility.Hidden;
        }

        private void OnClosing(object? sender, CancelEventArgs cancelEventArgs)
        {
            //if (_performanceAnalyzer != null)
            //{
            //    _performanceAnalyzer.StopCollectingStatistics();
            //    _performanceAnalyzer = null;
            //}

            DisposePerformanceCounters();

            if (_logMessagesWindow != null)
            {
                try
                {
                    _logMessagesWindow.Close();
                }
                catch
                {
                    // Maybe the window was already closed
                }

                _logMessagesWindow = null;
            }

            Log.RemoveLogListener(OnLogAction);
            UnregisterCurrentSceneView();
        }

        private void StartShowingStatistics()
        {
            if (SharpEngineSceneView != null)
            {
                ResultsTitleTextBlock.Visibility = Visibility.Visible;
                ResultsTitleTextBlock.Text = _showRenderingStatistics ? "Rendering statistics:" : "Camera info:";
            }
            else
            {
                ResultsTitleTextBlock.Visibility = Visibility.Collapsed;
            }

            AnalyerResultsTextBox.Visibility = Visibility.Collapsed;
            StatisticsTextBlock.Visibility = Visibility.Visible;


            SubscribeOnSceneRendered();

            // Setup PerformanceCounter
            if (ShowProcessCpuUsage)
                SetupPerformanceCounters();

            // Enable collecting statistics if it was not done yet
            if (SharpEngineSceneView != null && !SharpEngineSceneView.SceneView.IsCollectingStatistics)
            {
                SharpEngineSceneView.SceneView.IsCollectingStatistics = true;
                _isManuallyEnabledCollectingStatistics= true;

                SharpEngineSceneView.SceneView.Render(forceRender: true); // Force render so we get on statistics and are not showing empty data
            }
        }

        private void EndShowingStatistics()
        {
            StatisticsTextBlock.Visibility = Visibility.Collapsed;
            ResultsTitleTextBlock.Visibility = Visibility.Collapsed;

            UnsubscribeOnSceneRendered();
            DisposePerformanceCounters();

            if (_isManuallyEnabledCollectingStatistics)
            {
                if (SharpEngineSceneView != null)
                    SharpEngineSceneView.SceneView.IsCollectingStatistics = false;

                _isManuallyEnabledCollectingStatistics = true;
            }
        }

        private void RegisterSceneView(ISharpEngineSceneView? sharpEngineSceneView)
        {
            UnregisterCurrentSceneView();

            _sharpEngineSceneView = sharpEngineSceneView;

            if (sharpEngineSceneView == null)
                return;

            sharpEngineSceneView.ViewSizeChanged += SharpEngineSceneViewOnViewSizeChanged;

            sharpEngineSceneView.Disposing += OnSceneViewDisposing;

            if (sharpEngineSceneView.GpuDevice == null)
            {
                sharpEngineSceneView.GpuDeviceCreated += SharpEngineSceneViewOnGpuDeviceCreated;
                _isGpuDeviceCreatedSubscribed = true;
            }

            UpdateDeviceInfo();

            StartShowingStatistics();

            if (sharpEngineSceneView.SceneView.IsCollectingStatistics)
            {
                ResultsTitleTextBlock.Visibility = Visibility.Visible;

                if (sharpEngineSceneView.SceneView.Statistics != null)
                    UpdateStatistics(sharpEngineSceneView.SceneView.Statistics);
            }

            UpdateEnabledMenuItems();
        }

        private void SharpEngineSceneViewOnViewSizeChanged(object sender, ViewSizeChangedEventArgs e)
        {
            UpdateDeviceInfo();
        }

        private void SharpEngineSceneViewOnGpuDeviceCreated(object sender, GpuDeviceCreatedEventArgs e)
        {
            if (_sharpEngineSceneView != null)
                _sharpEngineSceneView.GpuDeviceCreated -= SharpEngineSceneViewOnGpuDeviceCreated;

            _isGpuDeviceCreatedSubscribed = false;

            UpdateDeviceInfo();
        }

        private void OnSceneViewDisposing(object? sender, bool disposing)
        {
            if (this.Dispatcher.CheckAccess())
                UnregisterCurrentSceneView();
            else
                this.Dispatcher.Invoke(() => UnregisterCurrentSceneView());
        }

        private void UnregisterCurrentSceneView()
        {
            if (_sharpEngineSceneView == null)
                return;

            UnsubscribeOnSceneRendered();
            DisposePerformanceCounters();

            _sharpEngineSceneView.Disposing -= OnSceneViewDisposing;
            _sharpEngineSceneView.ViewSizeChanged -= SharpEngineSceneViewOnViewSizeChanged;

            if (_isGpuDeviceCreatedSubscribed)
            {
                _sharpEngineSceneView.GpuDeviceCreated -= SharpEngineSceneViewOnGpuDeviceCreated;
                _isGpuDeviceCreatedSubscribed = false;
            }

            _sharpEngineSceneView = null;

            //if (_settingsEditorWindow != null)
            //{
            //    try
            //    {
            //        _settingsEditorWindow.Close();
            //    }
            //    catch
            //    {
            //        // Maybe the window was already closed
            //    }

            //    _settingsEditorWindow = null;
            //}

            //if (_renderingFilterWindow != null)
            //{
            //    try
            //    {
            //        _renderingFilterWindow.Close();
            //    }
            //    catch
            //    {
            //        // Maybe the window was already closed
            //    }

            //    _renderingFilterWindow = null;
            //}

            DeviceInfoTextBlock.Text = null;

            EndShowingStatistics();
        }
        
        private void UpdateDeviceInfo()
        {
            if (_sharpEngineSceneView == null || !_sharpEngineSceneView.SceneView.BackBuffersInitialized)
            {
                DeviceInfoTextBlock.Text = "SharpEngineSceneView is not initialized";
                return;
            }


            string viewInfo;
            var sceneView = _sharpEngineSceneView.SceneView;

            if (sceneView.BackBuffersInitialized)
            {
                int width = sceneView.Width;
                int height = sceneView.Height;

                viewInfo = string.Format("{0} x {1}", width, height);

                var multisampleCount = sceneView.UsedMultiSampleCount;
                if (multisampleCount > 1)
                    viewInfo += string.Format(" x {0}xMSAA", multisampleCount);

                // Supersampling is not yet supported
                //var supersamplingCount = sceneView.SupersamplingCount; // number of pixels used for one final pixel
                //if (supersamplingCount > 1)
                //    viewInfo += string.Format(" x {0}xSSAA", supersamplingCount);

                viewInfo += $" ({_sharpEngineSceneView.PresentationType})";
            }
            else
            {
                viewInfo = "";
            }


            if (_sharpEngineSceneView.GpuDevice != null)
            {
                string deviceInfoText = _sharpEngineSceneView.GpuDevice.GpuName;
                viewInfo = deviceInfoText + Environment.NewLine + viewInfo;
            }

            DeviceInfoTextBlock.Text = viewInfo;
        }

        private void SubscribeOnSceneRendered()
        {
            if (_isOnSceneRenderedSubscribed || _sharpEngineSceneView == null)
                return;

            _sharpEngineSceneView.SceneRendered += SceneViewOnSceneRendered;
            _isOnSceneRenderedSubscribed = true;
        }

        private void UnsubscribeOnSceneRendered()
        {
            if (!_isOnSceneRenderedSubscribed || _sharpEngineSceneView == null)
                return;

            if (_updateStatisticsTimer != null)
            {
                _updateStatisticsTimer.Stop();
                _updateStatisticsTimer = null;
            }

            _sharpEngineSceneView.SceneRendered -= SceneViewOnSceneRendered;
            _isOnSceneRenderedSubscribed = false;
        }

        private void SetupPerformanceCounters()
        {
            if (_processorsCount == 0)
            {
                try
                {
                    _processorsCount = Environment.ProcessorCount;
                }
                catch
                {
                    _processorsCount = 1;
                }
            }

            if (_cpuCounter == null)
            {
                try
                {
                    string processName = Process.GetCurrentProcess().ProcessName;
                    _cpuCounter = new PerformanceCounter("Process", "% Processor Time", processName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Error creating PerformanceCounter:\r\n" + ex.Message);
                    _cpuCounter = null;
                }

                // Try to get processor relative frequency counter
                try
                {
                    _processorFrequencyCounter = new PerformanceCounter("Processor information", "% of Maximum Frequency", "_Total");
                }
                catch
                {
                    _processorFrequencyCounter = null;
                }
            }

            // When do not render we still need to update cpu usage statistics - do that every seconds
            SetupUpdateStatisticsTimer(1000);
        }

        private void SetupUpdateStatisticsTimer(double milliseconds)
        {
            if (_updateStatisticsTimer == null)
            {
                _updateStatisticsTimer = new DispatcherTimer();
                _updateStatisticsTimer.Tick += CheckToUpdateStatisticsOrCameraInfo;
            }

            _updateStatisticsTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
            _updateStatisticsTimer.Start();
        }

        private void StopUpdateStatisticsTimer()
        {
            if (_updateStatisticsTimer != null)
                _updateStatisticsTimer.Stop();
        }
        
        private void DisposeUpdateStatisticsTimer()
        {
            if (_updateStatisticsTimer != null)
            {
                _updateStatisticsTimer.Stop();
                _updateStatisticsTimer = null;
            }
        }

        private void DisposePerformanceCounters()
        {
            DisposeUpdateStatisticsTimer();

            if (_cpuCounter != null)
            {
                _cpuCounter.Dispose();
                _cpuCounter = null;
            }

            if (_processorFrequencyCounter != null)
            {
                _processorFrequencyCounter.Dispose();
                _processorFrequencyCounter = null;
            }
        }

        private void CheckToUpdateStatisticsOrCameraInfo(object? sender, EventArgs eventArgs)
        {
            var elapsed = (DateTime.Now - _lastStatisticsUpdate).TotalMilliseconds;

            if (_updateStatisticsTimer != null && elapsed > (_updateStatisticsTimer.Interval.TotalMilliseconds * 0.9))
            {
                if (_showRenderingStatistics)
                {
                    if (SharpEngineSceneView?.SceneView.Statistics != null)
                        UpdateStatistics(SharpEngineSceneView.SceneView.Statistics);
                }
                else
                {
                    UpdateCameraInfo();
                }
            }
        }

        private void SceneViewOnSceneRendered(object? sender, EventArgs eventArgs)
        {
            // We also support scenario when the rendering is done on non-UI thread.
            // In this case we use Dispatcher.BeginInvoke to update the shown data on the UI thread

            RenderingStatistics? statistics;

            if (SharpEngineSceneView != null && SharpEngineSceneView.SceneView.Statistics != null)
                statistics = SharpEngineSceneView.SceneView.Statistics;
            else
                statistics = null;

            if (this.CheckAccess()) // Check if we are on UI thread
            {
                if (statistics != null)
                    UpdateStatistics(statistics);
                else
                    StatisticsTextBlock.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Non-UI thread - use Dispatcher.BeginInvoke
                if (statistics != null)
                {
                    statistics = statistics.Clone();
                    this.Dispatcher.BeginInvoke(new Action(delegate { UpdateStatistics(statistics); }));
                }
                else
                {
                    this.Dispatcher.BeginInvoke(new Action(delegate { StatisticsTextBlock.Visibility = Visibility.Collapsed; }));
                }
            }
        }

        private void StartStopCameraRotationMenuItem_OnClick(object sender, RoutedEventArgs args)
        {
            if (SharpEngineSceneView == null)
                return;

            var camera = SharpEngineSceneView.SceneView.Camera;

            if (camera == null)
                return;

            var rotatingCamera = camera as IRotatingCamera;

            if (rotatingCamera == null)
            {
                MessageBox.Show($"The used camera {camera.GetType().Name} does not support IRotatingCamera interface and cannot be animated");
                return;
            }

            if (rotatingCamera.IsRotating)
            {
                rotatingCamera.StopRotation();
                StartStopCameraRotationMenuItem.Header = "Start camera rotation";
            }
            else
            {
                rotatingCamera.StartRotation(50, 0);
                StartStopCameraRotationMenuItem.Header = "Stop camera rotation";
            }
        }
        
        private void DumpSceneNodesMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpSceneNodes();
        }

        private void DumpRenderingLayersMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpRenderingLayers();
        }

        private void DumpRenderingStepsMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpRenderingSteps();
        }

        private void DumpUsedMaterialsMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpUsedMaterials();
        }

        private void DumpMemoryMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpMemory();
        }
        
        private void DumpResourcesMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpResources();
        }
        
        private void DumpResourcesGroupByTypeMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpResourcesGroupByTypeName();
        }
        
        private void DumpResourcesForDelayedDisposalMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpResourcesForDelayedDisposal();
        }

        //private void DumpBackBufferChangesMenuItem_OnClick(object sender, RoutedEventArgs e)
        //{
        //    DumpBackBufferChanges();
        //}

        private void DumpSystemInfoMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            DumpSystemInfo();
        }
        
        private void ShowFullSceneDumpMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ShowFullSceneDump();
        }
        
        private void GetCameraDetailsMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            ShowCameraDetails();
        }

        private void GarbageCollectMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            GC.Collect();
            GC.WaitForFullGCComplete();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        
        private void LogWarningsPanel_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_logMessagesWindow != null)
                return;

            _logMessagesWindow = new LogMessagesWindow();

            _logMessagesWindow.MessageStartIndex = _deletedLogMessagesCount + 1;
            _logMessagesWindow.LogMessages = _logMessages;

            _logMessagesWindow.Closing += delegate(object? o, CancelEventArgs args)
            {
                _logMessagesWindow = null;

                // Check if user cleared the list of warnings
                if (_logMessages == null || _logMessages.Count == 0)
                {
                    WarningsCountTextBlock.Text = null;
                    LogWarningsPanel.Visibility = Visibility.Collapsed;
                }
            };

            _logMessagesWindow.Show();
        }

        private void OnShowCpuUsageCheckBoxCheckedChanged(object sender, RoutedEventArgs e)
        {
            ShowProcessCpuUsage = ShowCpuUsageCheckBox.IsChecked ?? false;

            // Close the menu
            ActionsRootMenuItem.IsSubmenuOpen = false;

            if (ShowProcessCpuUsage)
                SetupPerformanceCounters();
            else
               DisposePerformanceCounters();
        }

        private void ShowStatisticsButton_OnClick(object sender, RoutedEventArgs e)
        {
            StartShowingStatistics();
            ShowButtons(showStopPerformanceAnalyzerButton: false, showShowStatisticsButton: false);
        }

        private void AlwaysOnTopCheckBoxChanged(object sender, RoutedEventArgs e)
        {
            this.Topmost = (AlwaysOnTopCheckBox.IsChecked ?? false);

            // Close the menu
            ActionsRootMenuItem.IsSubmenuOpen = false;
        }

        private void ShowButtons(bool showStopPerformanceAnalyzerButton, bool showShowStatisticsButton)
        {
            //StopPerformanceAnalyzerButton.Visibility = showStopPerformanceAnalyzerButton ? Visibility.Visible : Visibility.Collapsed;
            ShowStatisticsButton.Visibility          = showShowStatisticsButton ? Visibility.Visible : Visibility.Collapsed;

            ButtonsPanel.Visibility = showStopPerformanceAnalyzerButton || showShowStatisticsButton ? Visibility.Visible : Visibility.Collapsed;
        }


        private string GetRenderingStatisticsDetails(RenderingStatistics renderingStatistics, string? fpsText)
        {
            if (fpsText == null)
                fpsText = "";

            if (fpsText.Length > 0 && !fpsText.Contains("("))
                fpsText = '(' + fpsText + ')';

            if (_renderingStatisticStringBuilder == null)
                _renderingStatisticStringBuilder = new StringBuilder();
            else
                _renderingStatisticStringBuilder.Clear();


            string commandBuffersRecordingTime;
            if (renderingStatistics.CommandBuffersRecordingTimeMs > 0.01)
                commandBuffersRecordingTime = $"CommandBuffersRecording: {renderingStatistics.CommandBuffersRecordingTimeMs:0.00} ms" + Environment.NewLine;
            else
                commandBuffersRecordingTime = "";

            string waitUntilRenderedTime;
            if (renderingStatistics.WaitUntilRenderedTimeMs > 0.01)
                waitUntilRenderedTime = $"WaitUntilRenderedTime: {renderingStatistics.WaitUntilRenderedTimeMs:0.00} ms" + Environment.NewLine;
            else
                waitUntilRenderedTime = "";
            
            string stagingUsageTime;
            if (renderingStatistics.StagingUsageTimeMs > 0.01)
                stagingUsageTime = $"StagingUsageTime: {renderingStatistics.StagingUsageTimeMs:0.00} ms" + Environment.NewLine;
            else
                stagingUsageTime = "";

            _renderingStatisticStringBuilder.AppendFormat(
                System.Globalization.CultureInfo.InvariantCulture,
@"Frame number: {0:#,##0}
CommandBuffer version: {1}
RenderingLayers version: {2}
Frame time: {3:0.00} ms {4}
UpdateTime: {5:0.00} ms
PrepareRenderTime: {6:0.00} ms
{7}CompleteRenderTime: {8:0.00} ms
{9}{10}UpdatedBuffers: Count: {11}; Size: {12}",
                renderingStatistics.FrameNumber,
                renderingStatistics.CommandBuffersRecordedCount,
                renderingStatistics.RenderingLayersRecreateCount,
                renderingStatistics.UpdateTimeMs + renderingStatistics.TotalRenderTimeMs,
                fpsText,
                renderingStatistics.UpdateTimeMs,
                renderingStatistics.PrepareRenderTimeMs,
                commandBuffersRecordingTime,
                renderingStatistics.CompleteRenderTimeMs,
                waitUntilRenderedTime,
                stagingUsageTime,
                renderingStatistics.UpdatedBuffersCount,
                FormatMemorySize(renderingStatistics.UpdatedBuffersSize));


            if (renderingStatistics.Other.Count > 0)
            {
                foreach (var keyValuePair in renderingStatistics.Other)
                {
                    var oneValue = keyValuePair.Value;

                    if (oneValue is null)
                        continue;

                    string oneValueText;

                    if (oneValue is string)
                        oneValueText = (string)oneValue;
                    else if (oneValue is float || oneValue is double)
                        oneValueText = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} ms", oneValue);
                    else
                        oneValueText = oneValue.ToString()!;

                    _renderingStatisticStringBuilder.AppendLine().Append(keyValuePair.Key).Append(": ").Append(oneValueText);
                }
            }


            if (renderingStatistics.DrawCallsCount > 0)
            {
                _renderingStatisticStringBuilder.AppendFormat(
                    System.Globalization.CultureInfo.InvariantCulture,
@"
CommandBuffersRecordingTime: {0:0.00} ms
DrawCallsCount: {1:#,##0}
DrawnVerticesCount: {2:#,##0}
DrawnIndicesCount: {3:#,##0}
VertexBuffersChangesCount: {4:#,##0}
IndexBuffersChangesCount: {5:#,##0}
DescriptorSetChangesCount: {6:#,##0}
PushConstantsChangesCount: {7:#,##0}
PipelineChangesCount: {8:#,##0}",
                    renderingStatistics.CommandBuffersRecordingTimeMs,
                    renderingStatistics.DrawCallsCount,
                    renderingStatistics.DrawnVerticesCount,
                    renderingStatistics.DrawnIndicesCount,
                    renderingStatistics.VertexBuffersChangesCount,
                    renderingStatistics.IndexBuffersChangesCount,
                    renderingStatistics.DescriptorSetChangesCount,
                    renderingStatistics.PushConstantsChangesCount,
                    renderingStatistics.PipelineChangesCount);
            }
            else if (_lastRenderingStatisticsWithRecorderCommandBuffers != null)
            {
                _renderingStatisticStringBuilder.AppendFormat(
                    System.Globalization.CultureInfo.InvariantCulture,
@"

Command buffer recorded in frame {0:#,##0}:
CommandBuffersRecordingTime: {1:0.00} ms
DrawCallsCount: {2:#,##0}
DrawnVerticesCount: {3:#,##0}
DrawnIndicesCount: {4:#,##0}
VertexBuffersChangesCount: {5:#,##0}
IndexBuffersChangesCount: {6:#,##0}
DescriptorSetChangesCount: {7:#,##0}
PipelineChangesCount: {8:#,##0}",
                    _lastRenderingStatisticsWithRecorderCommandBuffers.FrameNumber,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.CommandBuffersRecordingTimeMs,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.DrawCallsCount,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.DrawnVerticesCount,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.DrawnIndicesCount,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.VertexBuffersChangesCount,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.IndexBuffersChangesCount,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.DescriptorSetChangesCount,
                    _lastRenderingStatisticsWithRecorderCommandBuffers.PipelineChangesCount);
            }

            _renderingStatisticStringBuilder.AppendFormat("\r\n\r\nSceneViewDirtyFlags: {0}\r\nSceneDirtyFlags: {1}", _lastSceneViewDirtyFlags, _lastSceneDirtyFlags);


            if (renderingStatistics.Other.Count > 0)
            {
                _renderingStatisticStringBuilder.AppendLine().AppendLine();

                foreach (var keyValuePair in renderingStatistics.Other)
                    _renderingStatisticStringBuilder.AppendFormat("{0}: {1}\r\n", keyValuePair.Key, keyValuePair.Value);
            }

            return _renderingStatisticStringBuilder.ToString();
        }

        private static string FormatMemorySize(long size)
        {
            if (size >= 1024 * 1024 * 1024)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:#,##0.##} GB", (double)size / (1024 * 1024 * 1024));

            if (size >= 1024 * 1024)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##} MB", (double)size / (1024 * 1024));

            if (size >= 1024)
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.#} KB", (double)size / 1024);

            return string.Format("{0} B", size);
        }

        private void UpdateStatistics(RenderingStatistics renderingStatistics)
        {
            StopUpdateStatisticsTimer();

            double frameTime = renderingStatistics.UpdateTimeMs + renderingStatistics.TotalRenderTimeMs;
            double fps       = 1000 / frameTime;


            // Update average fps
            int averageResultsCount = 2000 / UpdateStatisticsInterval; // 2 seconds for default update interval (100) => averageResultsCount = 20 - every 20 statistical results we calculate an average
            if (averageResultsCount <= 1)
                averageResultsCount = 1;

            if (_fpsQueue == null)
                _fpsQueue = new Queue<double>(averageResultsCount);

            if (_fpsQueue.Count == averageResultsCount)
                _fpsQueue.Dequeue(); // dump the result that is farthest away

            _fpsQueue.Enqueue(fps);



            if (renderingStatistics.DrawCallsCount > 0)
            {
                // We store last RenderingStatistics that has any draw calls
                _lastRenderingStatisticsWithRecorderCommandBuffers = renderingStatistics.Clone();
            }

            if (SharpEngineSceneView?.SceneView.RenderingContext != null)
            {
                _lastSceneViewDirtyFlags = SharpEngineSceneView.SceneView.RenderingContext.SceneViewDirtyFlags;
                _lastSceneDirtyFlags     = SharpEngineSceneView.SceneView.RenderingContext.SceneDirtyFlags;
            }


            var now = DateTime.Now;

            if (UpdateStatisticsInterval > 0 && _lastStatisticsUpdate != DateTime.MinValue)
            {
                double elapsed = (now - _lastStatisticsUpdate).TotalMilliseconds;

                if (elapsed < UpdateStatisticsInterval) // Check if the required elapsed time has already passed
                {
                    // We skip showing the result for this frame, but set up timer so if there will no 
                    // additional frame rendered then we will show this frame info after the timer kick in.
                    SetupUpdateStatisticsTimer(UpdateStatisticsInterval * 3);

                    return;
                }
            }


            if (_showRenderingStatistics)
            {
                var statisticsText = GetRenderingStatisticsText(renderingStatistics, now, fps);
                StatisticsTextBlock.Text = statisticsText;
            }
            else
            {
                UpdateCameraInfo();
            }

            _lastStatisticsUpdate = now;

            ResultsTitleTextBlock.Visibility = Visibility.Visible;
            StatisticsTextBlock.Visibility = Visibility.Visible;
        }

        private void UpdateCameraInfo()
        {
            string cameraInfo;
            try
            {
                if (SharpEngineSceneView?.SceneView != null)
                    cameraInfo = SharpEngineSceneView.SceneView.GetCameraInfo(showMatrices: true);
                else
                    cameraInfo = "No SceneView";
            }
            catch (Exception ex)
            {
                cameraInfo = "Error getting camera info: " + ex.Message;
            }

            StatisticsTextBlock.Text = cameraInfo;
        }

        private string GetRenderingStatisticsText(RenderingStatistics renderingStatistics, DateTime now, double fps)
        {
            string averageFpsText;

            if (_fpsQueue != null && _fpsQueue.Count >= 10)
            {
                double averageFps = _fpsQueue.Average();
                averageFpsText = string.Format(System.Globalization.CultureInfo.InvariantCulture, "; avrg: {0:0.0}", averageFps);
            }
            else
            {
                averageFpsText = "";
            }

            string fpsText = String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} FPS{1}", fps, averageFpsText);


            string statisticsText;

            try
            {
                statisticsText = GetRenderingStatisticsDetails(renderingStatistics, fpsText);
            }
            catch (Exception ex)
            {
                statisticsText = "Error getting rendering statistics:\r\n" + ex.Message;
                if (ex.InnerException != null)
                    statisticsText += Environment.NewLine + ex.InnerException.Message;
            }

            if (ShowProcessCpuUsage)
            {
                float cpuUsage;

                if (_cpuCounter != null)
                {
                    var elapsed = (now - _lastPerfCountersReadTime).TotalMilliseconds;

                    if (elapsed >= 950) // To get accurate results we need to wait one second between reading perf values
                    {
                        try
                        {
                            cpuUsage = _cpuCounter.NextValue() / (float) _processorsCount;
                        }
                        catch
                        {
                            cpuUsage = _lastCpuUsage;
                        }

                        if (_processorFrequencyCounter != null)
                        {
                            try
                            {
                                float processorFrequency = _processorFrequencyCounter.NextValue(); // Get relative CPU frequency (from 0 to 100)

                                if (processorFrequency > 0)
                                    cpuUsage *= processorFrequency * 0.01f; // Adjust the cpu usage by relative frequency (this gives the same results as in Device Monitor)
                            }
                            catch
                            {
                                // pass
                            }
                        }

                        _lastCpuUsage             = cpuUsage;
                        _lastPerfCountersReadTime = now;
                    }
                    else
                    {
                        cpuUsage = _lastCpuUsage;
                    }
                }
                else
                {
                    cpuUsage = 0;
                }

                statisticsText += "\r\n\r\nProcess CPU usage:";

                if (cpuUsage > 0)
                    statisticsText += string.Format(System.Globalization.CultureInfo.InvariantCulture, " {0:0.0}%", cpuUsage);
            }

            return statisticsText;
        }

        private void DumpSceneNodes()
        {
            if (SharpEngineSceneView == null) 
                return;

            string dumpText;
            try
            {
                dumpText = GetSceneNodesInfo(SharpEngineSceneView.Scene);
            }
            catch (Exception ex)
            {
                dumpText = "Exception occurred when calling Scene.GetSceneNodesInfo:\r\n" + ex.Message;
            }

            dumpText += "\r\n\r\nLights:\r\n";

            foreach (var light in SharpEngineSceneView.Scene.Lights)
            {
                dumpText += "  " + light.ToString();
                dumpText += Environment.NewLine;
            }

            ShowInfoText(dumpText);
        }

        private string GetSceneNodesInfo(Scene scene)
        {
            return scene.GetSceneNodesInfo(showLocalBoundingBox: true); // all other parameters are already true by default
        }

        private void DumpUsedMaterials()
        {
            if (SharpEngineSceneView == null)
                return;

            string dumpText;

            try
            {
                dumpText = SharpEngineSceneView.Scene.GetUsedMaterialsInfo();
            }
            catch (Exception ex)
            {
                dumpText = "Exception occurred when calling Scene.GetUsedMaterialsDumpString:\r\n" + ex.Message;
            }

            ShowInfoText(dumpText);
        }

        private void DumpRenderingSteps()
        {
            if (SharpEngineSceneView == null)
                return;

            string dumpText;

            try
            {
                dumpText = SharpEngineSceneView.SceneView.GetRenderingStepsInfo();
            }
            catch (Exception ex)
            {
                dumpText = "Exception occurred when calling SceneView.GetRenderingStepsDumpString:\r\n" + ex.Message;
            }

            ShowInfoText(dumpText);
        }

        private void DumpRenderingLayers()
        {
            if (SharpEngineSceneView == null)
                return;

            string dumpText;

            try
            {
                dumpText = GetRenderingLayersInfo(SharpEngineSceneView.Scene);
            }
            catch (Exception ex)
            {
                dumpText = "Exception occurred when calling Scene.GetRenderingLayersInfo:\r\n" + ex.Message;
            }

            ShowInfoText(dumpText);
        }

        private string GetRenderingLayersInfo(Scene scene)
        {
            return scene.GetRenderingLayersInfo(dumpEmptyRenderingLayers: false, showSortedValue: true, showNativeHandles: true);
        }

        public void DumpMemory()
        {
            if (SharpEngineSceneView == null)
                return;

            string fullMemoryUsageDumpString;

            try
            {
                fullMemoryUsageDumpString = SharpEngineSceneView.Scene.GetFullMemoryUsageInfo(dumpAllActiveAllocations: true);
            }
            catch (Exception ex)
            {
                fullMemoryUsageDumpString = "Exception occurred when calling Scene.GetFullMemoryUsageDumpString:\r\n" + ex.Message;
            }

            ShowInfoText(fullMemoryUsageDumpString);
        }

        public void DumpResources()
        {
            if (SharpEngineSceneView == null || SharpEngineSceneView.GpuDevice == null)
                return;

            var reportText = "\r\nResources (classes derived from ComponentBase):\r\n" +
                             SharpEngineSceneView.GpuDevice.GetResourcesReportString(showFullTypeName: false, groupByTypeName: false, groupByIsDisposed: false);

            ShowInfoText(reportText);
        }
        
        public void DumpResourcesGroupByTypeName()
        {
            if (SharpEngineSceneView == null || SharpEngineSceneView.GpuDevice == null)
                return;

            var reportText = "\r\nResources (classes derived from ComponentBase):\r\n" +
                             SharpEngineSceneView.GpuDevice.GetResourcesReportString(showFullTypeName: false, groupByTypeName: true, groupByIsDisposed: false);

            ShowInfoText(reportText);
        }

        public void DumpResourcesForDelayedDisposal()
        {
            if (SharpEngineSceneView == null || SharpEngineSceneView.GpuDevice == null)
                return;

            var reportText = SharpEngineSceneView.GpuDevice.GetResourcesForDelayedDisposalString();

            if (string.IsNullOrEmpty(reportText))
                ShowInfoText("No resources scheduled to be disposed");
            else
                ShowInfoText("Resources scheduled to be disposed:\r\n" + reportText);
        }

        private string GetEngineSettingsDump()
        {
            if (SharpEngineSceneView == null)
                return "";

            var sb = new StringBuilder();

            if (SharpEngineSceneView.GpuDevice != null)
            {
                sb.Append("  VulkanDevice: ");
                DumpObjectProperties(SharpEngineSceneView.GpuDevice, sb, "  ");
                sb.AppendLine();
            }

            sb.Append("  Scene: ");
            DumpObjectProperties(SharpEngineSceneView.Scene, sb, "  ");
            sb.AppendLine();

            sb.Append("  SceneView: ");
            DumpObjectProperties(SharpEngineSceneView.SceneView, sb, "  ");
            sb.AppendLine();

            sb.Append("  SharpEngineSceneView: ");
            DumpObjectProperties(SharpEngineSceneView, sb, "  ");
            sb.AppendLine();
            
            return sb.ToString();
        }

        private void DumpObjectProperties(object? objectToDump, StringBuilder sb, string indent)
        {
            if (objectToDump == null)
            {
                sb.AppendLine("null");
                return;
            }

            var type = objectToDump.GetType();

            sb.AppendLine(type.Name + " properties:");

            try
            {
                var allProperties = type.GetProperties().OrderBy(p => p.Name).ToList();

                foreach (var propertyInfo in allProperties)
                {
                    if (propertyInfo.PropertyType.IsValueType || 
                        propertyInfo.PropertyType == typeof(string) ||
                        (propertyInfo.DeclaringType != null && 
                         propertyInfo.DeclaringType.Assembly.FullName != null && 
                         (propertyInfo.DeclaringType.Assembly.FullName.StartsWith("Ab3d.") || propertyInfo.DeclaringType.Assembly.FullName.StartsWith("Ab4d.")))) // Only show referenced objects for types that are declared in this class
                    {
                        string valueText;

                        try
                        {
                            var propertyValue = propertyInfo.GetValue(objectToDump, null);

                            if (propertyValue == null)
                                valueText = "<null>";
                            else
                                valueText = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}", propertyValue);
                        }
                        catch (Exception e)
                        {
                            valueText = "ERROR: " + e.Message;
                        }

                        sb.AppendLine(indent + propertyInfo.Name + ": " + valueText);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append(indent).Append("Error: ").AppendLine(ex.Message);
            }
        }

        private void DumpSystemInfo()
        {
            if (SharpEngineSceneView == null || SharpEngineSceneView.GpuDevice == null)
                return;

            string systemInfoText = GetSystemInfo(SharpEngineSceneView.GpuDevice);
            ShowInfoText(systemInfoText);
        }

        private void AddObjectFields(StringBuilder sb, object objectToDump, bool sortFields)
        {
            var allFields = objectToDump.GetType().GetFields();

            if (sortFields)
                allFields = allFields.OrderBy(f => f.Name).ToArray();

            foreach (var fieldInfo in allFields)
            {
                if (fieldInfo.FieldType.IsValueType && !fieldInfo.FieldType.IsPublic) // skip fixed buffers because they cannot be displayed (by observing what are the properties of a fixed buffer I saw that they have IsValueType = true and IsPublic = false (private value type)
                    continue;

                sb.AppendFormat("  {0}: ", fieldInfo.Name);

                var oneValue = fieldInfo.GetValue(objectToDump);

                if (oneValue == null)
                {
                    sb.AppendFormat("<null>\r\n", oneValue);
                }
                else if (fieldInfo.FieldType.IsArray)
                {
                    var array = (Array)oneValue;
                    for (int i = 0; i < array.Length; i++)
                        sb.Append(array.GetValue(i)).Append(" ");

                    sb.AppendLine();
                }
                else if (fieldInfo.FieldType == typeof(Bool32))
                {
                    var bool32 = (Bool32)oneValue;
                    sb.AppendFormat("{0}\r\n", bool32.Value == 1 ? "true" : "false");
                }
                else
                {
                    sb.AppendFormat("{0}\r\n", oneValue);
                }
            }
        }

        public string GetSystemInfo(VulkanDevice? vulkanDevice)
        {
            if (vulkanDevice == null)
                return "VulkanDevice is null";

            var sb = new StringBuilder();

            try
            {
                sb.AppendLine("All graphics cards (PhysicalDevices):");

                var allPhysicalDeviceDetails = vulkanDevice.VulkanInstance.AllPhysicalDeviceDetails;
                for (var i = 0; i < allPhysicalDeviceDetails.Length; i++)
                {
                    var physicalDeviceDetail = allPhysicalDeviceDetails[i];

                    sb.Append($"{i}: {physicalDeviceDetail.DeviceName} ({physicalDeviceDetail.DeviceProperties.DeviceType}, DeviceId: {physicalDeviceDetail.DeviceProperties.DeviceID}, DeviceLUID: ");

                    if (physicalDeviceDetail.IsDeviceLUIDValid)
                        sb.Append(physicalDeviceDetail.DeviceLUID);
                    else
                        sb.Append("unknown");


                    sb.Append(", DeviceUUID: ");

                    if (physicalDeviceDetail.IsDeviceUUIDValid && physicalDeviceDetail.DeviceUUID != null)
                        sb.Append(string.Join("", physicalDeviceDetail.DeviceUUID.Select(n => n.ToString("x"))));
                    else
                        sb.AppendLine("unknown");

                    sb.AppendLine(")");
                }

                sb.AppendLine();
                sb.Append("Selected PhysicalDevice: ").AppendLine(vulkanDevice.PhysicalDeviceDetails.DeviceName);

                sb.AppendLine("PhysicalDeviceDetails.Features:");
                AddObjectFields(sb, vulkanDevice.PhysicalDeviceDetails.PossibleFeatures, sortFields: true);

                if (vulkanDevice.PhysicalDeviceDetails.IsLineRasterizationExtensionSupported)
                {
                    sb.AppendLine("LineRasterizationFeatures:");
                    AddObjectFields(sb, vulkanDevice.PhysicalDeviceDetails.PossibleLineRasterizationFeatures, sortFields: true);
                }
                else
                {
                    sb.AppendLine("LineRasterizationFeatures: NOT SUPPORTED");
                }

                if (vulkanDevice.DefaultSurfaceDetails != null)
                {
                    sb.AppendLine("\r\n\r\nDefaultSurfaceDetails.SurfaceCapabilities:");
                    AddObjectFields(sb, vulkanDevice.DefaultSurfaceDetails.SurfaceCapabilities, sortFields: true);
                }

                sb.AppendLine("\r\n\r\nPhysicalDeviceDetails.DeviceProperties.Limits:");
                AddObjectFields(sb, vulkanDevice.PhysicalDeviceDetails.DeviceProperties.Limits, sortFields: true);

                // Now display DeviceProperties.limits that are defined as fixed arrays (see comments in PhysicalDeviceLimitsEx)
                AddObjectFields(sb, vulkanDevice.PhysicalDeviceDetails.PhysicalDeviceLimitsEx, sortFields: false);
            }
            catch (Exception ex)
            {
                sb.AppendLine("Error getting system info: \r\n" + ex.Message);
            }

            return sb.ToString();
        }

        private void ShowInfoText(string infoText)
        {
            System.IO.File.WriteAllText(DumpFileName, infoText);
            StartProcess(DumpFileName);
        }

        private static void StartProcess(string fileName)
        {
            try
            {
                // For CORE3 project we need to set UseShellExecute to true,
                // otherwise a "The specified executable is not a valid application for this OS platform" exception is thrown.
                System.Diagnostics.Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
            }
            catch
            {
                // pass
            }
        }

        private void SaveToBitmapMenuItem_OnClick(object sender, RoutedEventArgs args)
        {
            if (SharpEngineSceneView == null || _wpfBitmapIO.IsFileFormatExportSupported("png"))
                return;

            var renderedRawImage = SharpEngineSceneView.SceneView.RenderToRawImageData();

            string fileName = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SharpEngine.png");

            _wpfBitmapIO.SaveBitmap(renderedRawImage, fileName);
            System.Diagnostics.Process.Start(new ProcessStartInfo(fileName) { UseShellExecute = true });
        }
        
        private void CaptureInRenderDocMenuItem_OnClick(object sender, RoutedEventArgs args)
        {
            if (SharpEngineSceneView == null || !SharpEngineSceneView.SceneView.BackBuffersInitialized)
                return;

            bool isRenderDocAvailable = SharpEngineSceneView.SceneView.CaptureNextFrameInRenderDoc();

            if (!isRenderDocAvailable)
            {
                MessageBox.Show("Start the application from RenderDoc to be able to capture frames.");
                return;
            }

            SharpEngineSceneView.RenderScene(forceRender: true, forceUpdate: false);
        }

        private void SaveRenderedBitmap(BitmapSource? renderedBitmap, bool openSavedImage = true, string? initialFileName = null, string? dialogTitle = null)
        {
            if (renderedBitmap == null)
            {
                MessageBox.Show("No rendered image");
                return;
            }

            var saveFileDialog = new Microsoft.Win32.SaveFileDialog()
            {
                AddExtension = true,
                CheckFileExists = false,
                CheckPathExists = true,
                OverwritePrompt = true,
                ValidateNames = false,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                FileName = initialFileName ?? "SharpEngineRender.png",
                DefaultExt = "txt",
                Filter = "png Image (*.png)|*.png",
                Title = dialogTitle ?? "Select file name to store the rendered image"
            };

            if (saveFileDialog.ShowDialog() ?? false)
            {
                // write the bitmap to a file
                using (var imageStream = new FileStream(saveFileDialog.FileName, FileMode.Create))
                {
                    //JpegBitmapEncoder enc = new JpegBitmapEncoder();
                    var enc = new PngBitmapEncoder();
                    var bitmapImage = BitmapFrame.Create(renderedBitmap);
                    enc.Frames.Add(bitmapImage);
                    enc.Save(imageStream);
                }

                if (openSavedImage)
                    StartProcess(saveFileDialog.FileName);
            }
        }

        private void ShowFullSceneDump()
        {
            if (SharpEngineSceneView == null)
                return;

            // Start with empty DumpFile 
            System.IO.File.WriteAllText(DumpFileName, "Ab4d.SharpEngine FULL SCENE DUMP\r\n\r\n");

            if (SharpEngineSceneView.GpuDevice != null)
            {
                try
                {
                    string systemInfoText;
                    try
                    {
                        systemInfoText = GetSystemInfo(SharpEngineSceneView.GpuDevice);
                    }
                    catch (Exception ex)
                    {
                        systemInfoText = "Error getting system info: \r\n" + ex.Message;
                    }

                    AppendDumpText("System info:", systemInfoText);
                }
                catch (Exception ex)
                {
                    AppendDumpText("Error writing system info:", ex.Message);
                }
            }


            try
            {
                var sharpEngineSettingsDump = GetEngineSettingsDump();
                AppendDumpText("Engine settings:", sharpEngineSettingsDump);


                string dumpText;
                try
                {
                    dumpText = GetSceneNodesInfo(SharpEngineSceneView.Scene);
                    AppendDumpText("SceneNodes:", dumpText);
                }
                catch (Exception ex)
                {
                    AppendDumpText("Exception calling Scene.GetSceneNodesDumpString:", ex.Message);
                }


                string lightText = "";
                foreach (var light in SharpEngineSceneView.Scene.Lights)
                    lightText += "  " + light.ToString() + Environment.NewLine;

                AppendDumpText("Lights:", lightText);


                try
                {
                    dumpText = GetRenderingLayersInfo(SharpEngineSceneView.Scene);
                    AppendDumpText("RenderingLayers:", dumpText);
                }
                catch (Exception ex)
                {
                    AppendDumpText("Exception occurred when calling Scene.GetRenderingLayersDumpString:", ex.Message);
                }


                var cameraInfoDumpString = SharpEngineSceneView.SceneView.GetCameraInfo(showMatrices: true);
                AppendDumpText("Camera info:", cameraInfoDumpString);


                //var renderedToBitmap = SharpEngineSceneView.SceneView.RenderToBitmap(renderNewFrame: false);
                //string renderedBitmapBase64String = GetRenderedBitmapBase64String(renderedBitmap);

                //AppendDumpText("Rendered bitmap:", "<html><body>\r\n<img src=\"data:image/png;base64,\r\n" +
                //                                   renderedBitmapBase64String +
                //                                   "\" />\r\n</body></html>\r\n");
            }
            catch (Exception ex)
            {
                AppendDumpText("Error writing scene dump:", ex.Message);
            }

            StartProcess(DumpFileName);
        }

        private void AppendDumpText(string title, string content)
        {
            System.IO.File.AppendAllText(DumpFileName, title + "\r\n\r\n" + content + "\r\n##########################\r\n\r\n");
        }

        private string GetRenderedBitmapBase64String(WriteableBitmap? renderedBitmap)
        {
            if (renderedBitmap == null)
                return "RenderedBitmap is null";

            byte[] bitmapBytes;

            // write bitmap to a MemoryStream
            using (var imageStream = new MemoryStream())
            {
                //JpegBitmapEncoder enc = new JpegBitmapEncoder();
                PngBitmapEncoder enc = new PngBitmapEncoder();
                BitmapFrame bitmapImage = BitmapFrame.Create(renderedBitmap);
                enc.Frames.Add(bitmapImage);
                enc.Save(imageStream);

                imageStream.Seek(0, SeekOrigin.Begin);

                bitmapBytes = new byte[imageStream.Length];
                imageStream.Read(bitmapBytes, 0, bitmapBytes.Length);
            }

            string bitmapString = Convert.ToBase64String(bitmapBytes);

            // Format base64 string with adding new line chars after each 128 chars
            int stringLength = bitmapString.Length;
            if (stringLength > 500)
            {
                int segmentLength = 128;
                var sb = new StringBuilder((int)(stringLength + ((stringLength * 2) / segmentLength)));
                for (int i = 0; i < bitmapString.Length; i += segmentLength)
                {
                    if (i + segmentLength > stringLength)
                        sb.AppendLine(bitmapString.Substring(i));
                    else
                        sb.AppendLine(bitmapString.Substring(i, segmentLength));
                }

                bitmapString = sb.ToString();
            }

            return bitmapString;
        }

        private void ShowCameraDetails()
        {
            if (SharpEngineSceneView == null)
                return;

            var cameraInfoDumpString = SharpEngineSceneView.SceneView.GetCameraInfo(showMatrices: true);
            
            ShowInfoText(cameraInfoDumpString);
        }

        private void StatisticsTypeRadioButton_OnChecked(object sender, RoutedEventArgs e)
        {
            if (!this.IsLoaded)
                return;

            // Store value in local field so we do not need to check the value ShowRenderingStatisticsRadioButton.IsChecked on each update (this is quite slow)
            _showRenderingStatistics = ShowRenderingStatisticsRadioButton.IsChecked ?? false;

            if (_showRenderingStatistics)
            {
                ResultsTitleTextBlock.Text = "Rendering statistics:";
                StatisticsTextBlock.ClearValue(FontFamilyProperty);
                StatisticsTextBlock.ClearValue(FontSizeProperty);

                if (SharpEngineSceneView != null &&
                    SharpEngineSceneView.SceneView.IsCollectingStatistics &&
                    SharpEngineSceneView.SceneView.Statistics != null)
                {
                    UpdateStatistics(SharpEngineSceneView.SceneView.Statistics);
                }
            }
            else
            {
                ResultsTitleTextBlock.Text = "Camera info:";
                StatisticsTextBlock.FontFamily = new FontFamily("Courier New");
                StatisticsTextBlock.FontSize = 11;

                UpdateCameraInfo();
            }

            // Close the menu
            ActionsRootMenuItem.IsSubmenuOpen = false;
        }

        private void OnlineReferenceHelpMenuItem_OnClick(object sender, RoutedEventArgs e)
        {
            StartProcess("https://www.ab4d.com/help/SharpEngine/html/R_Project_Ab4d_SharpEngine.htm");
        }
    }
}