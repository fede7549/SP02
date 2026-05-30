using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

static byte[] ReadBuffer(IBuffer buffer)
{
    byte[] data = new byte[buffer.Length];
    DataReader.FromBuffer(buffer).ReadBytes(data);
    return data;
}

static void ProcesarDatos(byte[] data)
{
    string hex = BitConverter.ToString(data);

    if (data.Length >= 6 && data[0] == 0xFF && data[1] == 0x44)
    {
        int spo2 = data[4];
        int pulse = data[5];

        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] HEX: {hex}");
        Console.WriteLine($"SpO2: {spo2}% | Pulso: {pulse} bpm");

        if (spo2 >= 94)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Estado: OK");
        }
        else if (spo2 >= 90)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("PRECAUCION: SpO2 entre 90% y 93%");
        }
        else if (spo2 > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ALERTA CRITICA: SpO2 bajo 90%");
            Console.Beep(1000, 700);
        }

        Console.ResetColor();
    }
    else
    {
        Console.WriteLine($"Trama ignorada: {hex}");
    }
}

Console.WriteLine("Buscando OXIMETER por BLE...");
Console.WriteLine("Enciende el oxímetro y pon el dedo.");
Console.WriteLine();

ulong? bluetoothAddress = null;

var watcher = new BluetoothLEAdvertisementWatcher
{
    ScanningMode = BluetoothLEScanningMode.Active
};

watcher.Received += (sender, args) =>
{
    string name = args.Advertisement.LocalName;

    if (name.Contains("OXIMETER", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("HealthTree", StringComparison.OrdinalIgnoreCase))
    {
        bluetoothAddress = args.BluetoothAddress;
        Console.WriteLine($"Encontrado: {name} | MAC: {args.BluetoothAddress:X}");
        watcher.Stop();
    }
};

watcher.Start();

for (int i = 0; i < 20 && bluetoothAddress == null; i++)
{
    await Task.Delay(500);
}

watcher.Stop();

if (bluetoothAddress == null)
{
    Console.WriteLine("No se encontró el oxímetro.");
    Console.ReadLine();
    return;
}

Console.WriteLine("Conectando...");

BluetoothLEDevice? bleDevice =
    await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress.Value);

if (bleDevice == null)
{
    Console.WriteLine("No se pudo conectar al oxímetro.");
    Console.ReadLine();
    return;
}

Console.WriteLine($"Conectado a: {bleDevice.Name}");
Console.WriteLine($"Estado: {bleDevice.ConnectionStatus}");

Guid serviceUuid = Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");

GattDeviceServicesResult servicesResult =
    await bleDevice.GetGattServicesAsync(BluetoothCacheMode.Uncached);

if (servicesResult.Status != GattCommunicationStatus.Success)
{
    Console.WriteLine($"Error servicios: {servicesResult.Status}");
    Console.ReadLine();
    return;
}

var service = servicesResult.Services.FirstOrDefault(s => s.Uuid == serviceUuid);

if (service == null)
{
    Console.WriteLine("No se encontró servicio FFE0.");
    Console.ReadLine();
    return;
}

GattCharacteristicsResult charsResult =
    await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);

if (charsResult.Status != GattCommunicationStatus.Success)
{
    Console.WriteLine($"Error características: {charsResult.Status}");
    Console.ReadLine();
    return;
}

foreach (var ch in charsResult.Characteristics)
{
    if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
        continue;

    Console.WriteLine($"Activando Notify en: {ch.Uuid}");

    ch.ValueChanged += (sender, args) =>
    {
        byte[] data = ReadBuffer(args.CharacteristicValue);
        ProcesarDatos(data);
    };

    var status =
        await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

    Console.WriteLine($"Notify status: {status}");
}

Console.WriteLine();
Console.WriteLine("Leyendo SpO2 en tiempo real...");
Console.WriteLine("Presiona ENTER para cerrar.");
Console.ReadLine();