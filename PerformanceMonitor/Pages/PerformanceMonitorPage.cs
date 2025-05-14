// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using System.Text;

namespace PerformanceMonitor;

/// <summary>
/// Class for storing all performance metrics in one place
/// </summary>
internal class SystemPerformanceData
{
    // Process information
    public string TopCpuProcesses { get; set; } = "Loading process data...";
    public string TopMemoryProcesses { get; set; } = "Loading process data...";
    public string TopDiskProcesses { get; set; } = "Loading process data...";
    public string TopNetworkProcesses { get; set; } = "Loading process data...";

    // Current values
    public float CurrentCpuUsage { get; set; }
    public float CurrentMemoryUsage { get; set; }
    public float AvailableMemoryMB { get; set; }
    public float CurrentDiskUsage { get; set; }
    public float CurrentNetworkSentKBps { get; set; }
    public float CurrentNetworkReceivedKBps { get; set; }

    // System information
    public string ProcessorName { get; set; }
    public string DiskInformation { get; set; }
    public string NetworkInformation { get; set; }
}

[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

internal sealed partial class PerformanceMonitorPage : IListPage, IDisposable
{
    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _memoryCounter;
    private readonly PerformanceCounter[] _diskCounters;
    private readonly PerformanceCounter _networkSentCounter;
    private readonly PerformanceCounter _networkReceivedCounter;

    // System performance data object to store all metrics
    private SystemPerformanceData _performanceData = new SystemPerformanceData();

    private int _loadCount;
    private bool IsActive => _loadCount > 0;

    private List<ListItem> _items = new List<ListItem>();
    private ListItem _cpuItem;
    private ListItem _memoryItem;
    private ListItem _diskItem;
    private ListItem _networkItem;

    // Needed for IListItem

    public IFilters Filters => null;

    public IGridProperties GridProperties => null;

    public bool HasMoreItems => false;

    public string SearchText => string.Empty;

    public bool ShowDetails => true;

    public OptionalColor AccentColor => default;

    public bool IsLoading => false;

    public string Id => string.Empty;

    public void LoadMore()
    {
    }

    public string Title => "Performance monitor";

    public string PlaceholderText => "Performance monitor";

    public IIconInfo Icon => new IconInfo("\uE9D2"); // switch

    public string Name => "Performance monitor";

#pragma warning disable CS0067 // The event is never used
    public event TypedEventHandler<object, IPropChangedEventArgs> PropChanged;

    private event TypedEventHandler<object, IItemsChangedEventArgs> InternalItemsChanged;
#pragma warning restore CS0067 // The event is never used

    public ICommandItem EmptyContent => new CommandItem() { Icon = new IconInfo("\uE8AB"), Title = "This page starts empty", Subtitle = "but go back and open it again" };

    // Start of code

    public PerformanceMonitorPage()
    {
        // Initialize CPU counter
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter.NextValue(); // First call always returns 0

        _cpuItem = new ListItem(new NoOpCommand())
        {
            Icon = new IconInfo("\uE9D9"), // CPU icon
            Title = "CPU",
            Details = new Details() { Body = "Loading..." }
        };
        _items.Add(_cpuItem);

        // Initialize Memory counter
        _memoryCounter = new PerformanceCounter("Memory", "Available MBytes");

        _memoryItem = new ListItem(new NoOpCommand())
        {
            Icon = new IconInfo("\uE964"), // Memory icon
            Title = "Memory",
            Details = new Details() { Body = "Loading..." }
        };
        _items.Add(_memoryItem);

        // Initialize Disk counters (for all physical disks)
        var diskNames = GetPhysicalDiskNames();
        _diskCounters = new PerformanceCounter[diskNames.Length];
        for (int i = 0; i < diskNames.Length; i++)
        {
            _diskCounters[i] = new PerformanceCounter("PhysicalDisk", "% Disk Time", diskNames[i]);
            _diskCounters[i].NextValue(); // First call always returns 0
        }

        _diskItem = new ListItem(new NoOpCommand())
        {
            Icon = new IconInfo("\uE977"), // Disk icon
            Title = "Disk",
            Details = new Details() { Body = "Loading..." }
        };
        _items.Add(_diskItem);

        // Try to initialize Network counters (may not be available on all systems)
        try
        {
            string networkInterface = GetMostActiveNetworkInterface();
            if (!string.IsNullOrEmpty(networkInterface))
            {
                _networkSentCounter = new PerformanceCounter("Network Interface", "Bytes Sent/sec", networkInterface);
                _networkReceivedCounter = new PerformanceCounter("Network Interface", "Bytes Received/sec", networkInterface);
                _networkSentCounter.NextValue(); // First call always returns 0
                _networkReceivedCounter.NextValue(); // First call always returns 0

                _networkItem = new ListItem(new NoOpCommand())
                {
                    Icon = new IconInfo("\uEC05"), // Network icon
                    Title = "Network",
                    Details = new Details() { Body = "Loading..." }
                };
                _items.Add(_networkItem);
            }
        }
        catch (Exception)
        {
        }

        // Initialize performance data
        _performanceData.DiskInformation = GetDiskInformation();
    }

    public event TypedEventHandler<object, IItemsChangedEventArgs>? ItemsChanged
    {
        add
        {
            InternalItemsChanged += value;
            _loadCount++;
            Task.Run(() =>
            {
                UpdateValues();
            });
        }

        remove
        {
            InternalItemsChanged -= value;
            _loadCount--;
        }
    }

    private Details GetCPUDetails()
    {
        return new Details()
        {
            Title = "CPU Details",
            Body = $@"## Top CPU Processes

{_performanceData.TopCpuProcesses}

## Processor Information

- Number of Cores: {Environment.ProcessorCount}
- Architecture: {RuntimeInformation.ProcessArchitecture}"
        };
    }

    private Details GetMemoryDetails()
    {


        return new Details()
        {
            Title = "Memory Details",
            Body = $@"## Top Memory Processes

{_performanceData.TopMemoryProcesses}

## Memory info

- Total Physical Memory: {GetTotalPhysicalMemoryGB():0.00} GB
- Available Memory: {_performanceData.AvailableMemoryMB / 1024:0.00} GB
- Memory In Use: {GetUsedMemoryGB():0.00} GB"
        };
    }

    private Details GetDiskDetails()
    {
        return new Details()
        {
            Title = "Disk Details",
            Body = $@"## Top Disk Processes

{_performanceData.TopDiskProcesses}

## Disk Information

{_performanceData.DiskInformation}"
        };
    }

    private Details GetNetworkDetails()
    {
        return new Details()
        {
            Title = "Network Details",
            Body = $@"To be added in the future."
        };
    }

    private async void UpdateValues()
    {
        // Update interval in milliseconds
        const int updateInterval = 500;

        // TODO: Fix this behaviour which is needed cause of a bug
        while (_loadCount > 0)
        {
            // Record start time of update cycle
            var startTime = DateTime.Now;

            var tasks = new List<Task>();

            // Start all update tasks in parallel
            if (_cpuItem != null)
                tasks.Add(Task.Run(() => UpdateCpuValues()));

            if (_memoryItem != null)
                tasks.Add(Task.Run(() => UpdateMemoryValues()));

            if (_diskCounters.Length > 0 && _diskItem != null)
                tasks.Add(Task.Run(() => UpdateDiskValues()));

            if (_networkItem != null)
                tasks.Add(Task.Run(() => UpdateNetworkValues()));

            tasks.Add(GetProcessInfo());

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);

            // Calculate how much time has passed
            var elapsedTime = (DateTime.Now - startTime).TotalMilliseconds;

            // If we completed faster than our desired interval, wait the remaining time
            if (elapsedTime < updateInterval)
            {
                await Task.Delay((int)(updateInterval - elapsedTime));
            }
        }
    }

    private void UpdateCpuValues()
    {
        // Quick update
        _performanceData.CurrentCpuUsage = _cpuCounter.NextValue();
        _cpuItem.Title = $"CPU - {_performanceData.CurrentCpuUsage:0.0}%";
        _cpuItem.Details = GetCPUDetails();
    }

    private void UpdateMemoryValues()
    {
        // Quick update
        _performanceData.AvailableMemoryMB = _memoryCounter.NextValue();
        _performanceData.CurrentMemoryUsage = 100f - (_performanceData.AvailableMemoryMB / GetTotalPhysicalMemory() * 100f);
        _memoryItem.Title = $"Memory - {_performanceData.CurrentMemoryUsage:0.0}%";
        _memoryItem.Details = GetMemoryDetails();
    }

    private void UpdateDiskValues()
    {
        // Quick update
        if (_diskCounters.Length > 0)
        {
            _performanceData.CurrentDiskUsage = _diskCounters.Average(counter => counter.NextValue());
        }
        _diskItem.Title = $"Disk - {_performanceData.CurrentDiskUsage:0.0}%";
        _diskItem.Details = GetDiskDetails();
    }

    private void UpdateNetworkValues()
    {
        // Quick update
        _performanceData.CurrentNetworkSentKBps = _networkSentCounter.NextValue() / 1024; // Convert to KB/s
        _performanceData.CurrentNetworkReceivedKBps = _networkReceivedCounter.NextValue() / 1024; // Convert to KB/s
        _networkItem.Title = $"Network - {_performanceData.CurrentNetworkReceivedKBps:0.0} KB/s ↓, {_performanceData.CurrentNetworkSentKBps:0.0} KB/s ↑";
        _networkItem.Details = GetNetworkDetails();
    }

    public IListItem[] GetItems()
    {
        return _items.ToArray();
    }

    // === Helper functions ===

    private async Task<bool> GetProcessInfo()
    {
        int pollingTime = 750;

        try
        {
            var initialProcessValues = Process.GetProcesses()
            .Where(p => !string.IsNullOrEmpty(p.ProcessName))
            .Select(p =>
            {
                try
                {
                    if (p.HasExited) return null;

                    if (GetProcessIoCounters(p.Handle, out IO_COUNTERS counters))
                    {
                        ulong readVal = counters.ReadTransferCount;
                        ulong writeVal = counters.WriteTransferCount;
                        return new
                        {
                            Id = p.Id,
                            Name = p.ProcessName,
                            readVal,
                            writeVal,
                            totalProcessTime = p.TotalProcessorTime,
                            workingSet = p.WorkingSet64,
                        };
                    }
                }
                catch { return null; }

                return null;
            })
            .Where(p => p != null)
            .ToDictionary(p => p.Id, p => p);

            await Task.Delay(pollingTime); // Wait a bit to measure usage

            var finalProcessValues = Process.GetProcesses()
            .Where(p => !string.IsNullOrEmpty(p.ProcessName))
            .Select(p =>
            {
                try
                {
                    if (p.HasExited) return null;

                    if (GetProcessIoCounters(p.Handle, out IO_COUNTERS counters))
                    {
                        ulong readVal = counters.ReadTransferCount;
                        ulong writeVal = counters.WriteTransferCount;
                        return new
                        {
                            Id = p.Id,
                            readVal,
                            writeVal,
                            totalProcessTime = p.TotalProcessorTime,
                        };
                    }
                }
                catch { return null; }

                return null;
            })
            .Where(p => p != null)
            .ToDictionary(p => p.Id, p => p);

            // Make new dictionary with finalizedProcesses
            var finalizedProcesses = new Dictionary<int, (string Name, ulong readVal, ulong writeVal, double totalProcessTime, long workingSet)>();

            foreach (var (key, value) in finalProcessValues)
            {
                if (initialProcessValues.TryGetValue(key, out var initialValue))
                {
                    ulong readVal = value.readVal - initialValue.readVal;
                    ulong writeVal = value.writeVal - initialValue.writeVal;
                    double totalProcessTime = (value.totalProcessTime - initialValue.totalProcessTime).TotalMilliseconds;
                    finalizedProcesses[key] = (initialValue.Name, readVal, writeVal, totalProcessTime, initialValue.workingSet);
                }
            }

            double secondConversion = 1000.0 / pollingTime;

            // Format the string for CPU usage
            var cpuString = new StringBuilder();

            var topCPUProcesses = finalizedProcesses
                .OrderByDescending(p => p.Value.totalProcessTime)
                .Take(5)
                .ToDictionary(p => p.Key, p => p.Value);

            foreach (var (key, value) in topCPUProcesses)
            {
                double cpuUsage = value.totalProcessTime * 100.0 / (pollingTime * Environment.ProcessorCount);
                cpuUsage = Math.Min(100, Math.Max(0, cpuUsage)); // Clamp between 0-100%
                cpuString.AppendLine($"- {value.Name}: {cpuUsage:0.0}% CPU");
            }

            _performanceData.TopCpuProcesses = cpuString.ToString();

            // Format the string for memory usage
            var memoryString = new StringBuilder();

            var topMemoryProcesses = finalizedProcesses
                .OrderByDescending(p => p.Value.workingSet)
                .Take(5)
                .ToDictionary(p => p.Key, p => p.Value);

            foreach (var (key, value) in topMemoryProcesses)
            {
                memoryString.AppendLine($"- {value.Name}: {value.workingSet / 1024 / 1024:0.0} MB");
            }

            _performanceData.TopMemoryProcesses = memoryString.ToString();

            // Format the string for disk usage
            var diskString = new StringBuilder();

            var topDiskProcesses = finalizedProcesses
                .OrderByDescending(p => p.Value.readVal)
                .Take(5)
                .ToDictionary(p => p.Key, p => p.Value);

            foreach (var (key, value) in topDiskProcesses)
            {
                diskString.AppendLine($"- {value.Name}: R: {value.readVal * secondConversion / 1024 / 1024:0.0} , W: {value.writeVal * secondConversion / 1024 / 1024:0.0} MB");
            }

            _performanceData.TopDiskProcesses = diskString.ToString();
        }
        catch (Exception e)
        {
            return false;
        }

        return true;
    }

    private float GetTotalPhysicalMemoryGB()
    {
        return GetTotalPhysicalMemory() / 1024; // Convert MB to GB
    }

    private float GetUsedMemoryGB()
    {
        return (GetTotalPhysicalMemory() - _performanceData.AvailableMemoryMB) / 1024; // Convert MB to GB
    }

    private string GetDiskInformation()
    {
        var result = new System.Text.StringBuilder();

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            double freeSpaceGB = drive.TotalFreeSpace / (1024.0 * 1024 * 1024);
            double totalSpaceGB = drive.TotalSize / (1024.0 * 1024 * 1024);
            double usedPercent = 100 - (freeSpaceGB / totalSpaceGB * 100);

            result.AppendLine($"### Drive {drive.Name} ({drive.VolumeLabel}):");
            result.AppendLine("");
            result.AppendLine($"{freeSpaceGB:0.00} GB free of {totalSpaceGB:0.00} GB");
            result.AppendLine("");
            result.AppendLine($"{usedPercent:0.0}% used");
            result.AppendLine("");

            // Add a simple visualization
            int usedBlocks = (int)(usedPercent / 10);
            result.AppendLine($"  - [{new string('⬛', usedBlocks)}{new string('⬜', 10 - usedBlocks)}]");
        }

        return result.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1;
        public byte localPort2;
        public byte localPort3;
        public byte localPort4;
        public uint remoteAddr;
        public byte remotePort1;
        public byte remotePort2;
        public byte remotePort3;
        public byte remotePort4;
        public int owningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        bool sort, int ipVersion, int tblClass, int reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);

    private static string[] GetPhysicalDiskNames()
    {
        var category = new PerformanceCounterCategory("PhysicalDisk");
        var instanceNames = category.GetInstanceNames();
        return instanceNames.Where(name => name != "_Total").ToArray();
    }

    private static string GetMostActiveNetworkInterface()
    {
        var category = new PerformanceCounterCategory("Network Interface");
        return category.GetInstanceNames().FirstOrDefault(name => !name.Contains("Loopback"));
    }

    private static float GetTotalPhysicalMemory()
    {
        return (float)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024); // Convert bytes to MB
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _memoryCounter?.Dispose();

        if (_diskCounters != null)
        {
            foreach (var counter in _diskCounters)
            {
                counter?.Dispose();
            }
        }

        _networkSentCounter?.Dispose();
        _networkReceivedCounter?.Dispose();
    }
}