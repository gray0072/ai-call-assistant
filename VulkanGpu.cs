using System.Runtime.InteropServices;

namespace AiCallAssistant;

/// <summary>
/// Finds the discrete GPU among Vulkan devices. On laptops with an integrated GPU as well,
/// whisper.cpp picks device 0 — usually the integrated one, which is slow or even crashes.
/// </summary>
static class VulkanGpu
{
    const int DeviceTypeIntegrated = 1;
    const int DeviceTypeDiscrete = 2;

    /// <summary>
    /// Index of the first discrete GPU in whisper.cpp's device list (it lists discrete and
    /// integrated GPUs in Vulkan enumeration order), or null when there is none.
    /// </summary>
    public static (int Index, string Name)? FindDiscrete()
    {
        try
        {
            var info = new InstanceCreateInfo { SType = 1 }; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
            if (vkCreateInstance(ref info, IntPtr.Zero, out var instance) != 0)
                return null;
            try
            {
                uint count = 0;
                vkEnumeratePhysicalDevices(instance, ref count, null);
                var devices = new IntPtr[count];
                vkEnumeratePhysicalDevices(instance, ref count, devices);

                int gpuIndex = 0;
                var props = new byte[4096]; // VkPhysicalDeviceProperties is ~830 bytes
                foreach (var device in devices)
                {
                    vkGetPhysicalDeviceProperties(device, props);
                    int type = BitConverter.ToInt32(props, 16);
                    if (type != DeviceTypeDiscrete && type != DeviceTypeIntegrated)
                        continue;
                    if (type == DeviceTypeDiscrete)
                    {
                        var name = System.Text.Encoding.UTF8.GetString(props, 20, 256).TrimEnd('\0');
                        return (gpuIndex, name);
                    }
                    gpuIndex++;
                }
                return null;
            }
            finally
            {
                vkDestroyInstance(instance, IntPtr.Zero);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null; // no Vulkan runtime
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct InstanceCreateInfo
    {
        public int SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr ApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr EnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr EnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")]
    static extern int vkCreateInstance(ref InstanceCreateInfo createInfo, IntPtr allocator, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);

    [DllImport("vulkan-1.dll")]
    static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, [Out] IntPtr[]? devices);

    [DllImport("vulkan-1.dll")]
    static extern void vkGetPhysicalDeviceProperties(IntPtr device, [Out] byte[] properties);
}
