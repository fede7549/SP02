using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using System.Net.Http;
using System.Text;

namespace SatO2_WinForms
{
    public partial class Form1 : Form
    {
        private BluetoothLEAdvertisementWatcher? _watcher;
        private BluetoothLEDevice? _bleDevice;
        private bool _leyendo = false;

        private DateTime? _inicioPrecaucion = null;
        private DateTime? _inicioAlerta = null;
        private DateTime _ultimaAlarma = DateTime.MinValue;

        private DateTime _ultimaLectura = DateTime.MinValue;
        private System.Windows.Forms.Timer _timerConexion = new();

        private CancellationTokenSource? _alarmaCts;
        private string _alarmaActual = "";

        private readonly object _csvLock = new();

        private Label lblSpo2 = new();
        private Label lblPulso = new();
        private Label lblEstado = new();
        private Button btnIniciar = new();
        private Button btnDetener = new();
        private TextBox txtLog = new();

        //ALERTA DE DISCORD
        private static readonly HttpClient _http = new();

        private string _discordWebhookUrl = "https://discordapp.com/api/webhooks/1508288952661053540/4AZn2kJkNk9P8mpwrP3dsieMGB7QCQmMnjbWUWifRk1u2-bCaGC1r5ghcLEDnrBirifx";

        private DateTime _ultimoMensajePrecaucion = DateTime.MinValue;
        private DateTime _ultimoMensajeAlerta = DateTime.MinValue;
        private DateTime _ultimoMensajeCritico = DateTime.MinValue;
        private DateTime _ultimoMensajeConexion = DateTime.MinValue;

        private string _ultimoEstadoAlerta = "OK";
        private DateTime _ultimoEnvioRepetido = DateTime.MinValue;

        public Form1()
        {
            CrearInterfaz();
        }

        private void CrearInterfaz()
        {
            Text = "Monitor SpO2 Papa";
            Width = 650;
            Height = 500;
            StartPosition = FormStartPosition.CenterScreen;

            lblSpo2.Text = "SpO2: -- %";
            lblSpo2.Font = new Font("Segoe UI", 36, FontStyle.Bold);
            lblSpo2.AutoSize = true;
            lblSpo2.Left = 30;
            lblSpo2.Top = 30;

            lblPulso.Text = "Pulso: -- bpm";
            lblPulso.Font = new Font("Segoe UI", 28, FontStyle.Bold);
            lblPulso.AutoSize = true;
            lblPulso.Left = 30;
            lblPulso.Top = 110;

            lblEstado.Text = "Estado: Sin lectura";
            lblEstado.Font = new Font("Segoe UI", 20, FontStyle.Bold);
            lblEstado.AutoSize = true;
            lblEstado.Left = 30;
            lblEstado.Top = 180;

            btnIniciar.Text = "Iniciar lectura";
            btnIniciar.Width = 150;
            btnIniciar.Height = 40;
            btnIniciar.Left = 30;
            btnIniciar.Top = 240;
            btnIniciar.Click += async (_, _) => await IniciarLectura();

            btnDetener.Text = "Detener";
            btnDetener.Width = 150;
            btnDetener.Height = 40;
            btnDetener.Left = 200;
            btnDetener.Top = 240;
            btnDetener.Click += (_, _) => DetenerLectura();

            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Left = 30;
            txtLog.Top = 300;
            txtLog.Width = 570;
            txtLog.Height = 130;

            Controls.Add(lblSpo2);
            Controls.Add(lblPulso);
            Controls.Add(lblEstado);
            Controls.Add(btnIniciar);
            Controls.Add(btnDetener);
            Controls.Add(txtLog);

            _timerConexion.Interval = 5000;

            _timerConexion.Tick += (_, _) =>
            {
                if (!_leyendo)
                    return;

                if (_ultimaLectura == DateTime.MinValue)
                    return;

                double segundos = (DateTime.Now - _ultimaLectura).TotalSeconds;

                if (segundos > 10)
                {
                    lblEstado.Text = $"SIN DATOS hace {segundos:0}s";
                    lblEstado.ForeColor = Color.DarkRed;

                    Log($"ALERTA: datos congelados hace {segundos:0} segundos. Reconectando...");

                    IniciarAlarmaRepetitiva("SIN_DATOS");

                    if ((DateTime.Now - _ultimoMensajeConexion).TotalSeconds > 15)
                    {
                        _ultimoMensajeConexion = DateTime.Now;

                        _ = EnviarDiscordAsync(
                            $"SIN_DATOS: oximetro sin datos hace {segundos:0} segundos. Posible desconexion/apagado. Revisar ahora.");
                    }

                    _ultimaLectura = DateTime.Now;

                    _ = ReconectarAsync();
                }
            };

            _timerConexion.Start();
        }

        private void GuardarCsv(int spo2, int pulse, string estado, string hex)
        {
            try
            {
                string carpeta = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "SatO2_Registros");

                Directory.CreateDirectory(carpeta);

                string archivo = Path.Combine(
                    carpeta,
                    $"SatO2_{DateTime.Now:yyyy-MM-dd}.csv");

                bool existe = File.Exists(archivo);

                lock (_csvLock)
                {
                    using StreamWriter sw = new StreamWriter(archivo, append: true);

                    if (!existe)
                    {
                        sw.WriteLine("fecha,hora,spo2,pulso,estado,hex");
                    }

                    sw.WriteLine($"{DateTime.Now:yyyy-MM-dd},{DateTime.Now:HH:mm:ss},{spo2},{pulse},{estado},{hex}");
                }
            }
            catch (Exception ex)
            {
                Log($"Error guardando CSV: {ex.Message}");
            }
        }
        private async Task ReconectarAsync()
        {
            try
            {
                try { _watcher?.Stop(); } catch { }
                try { _bleDevice?.Dispose(); } catch { }

                _bleDevice = null;
                _leyendo = false;

                await Task.Delay(2000);

                await IniciarLectura();
            }
            catch (Exception ex)
            {
                Log($"Error reconectando: {ex.Message}");
            }
        }

        private async Task IniciarLectura()
        {
            _ultimaLectura = DateTime.MinValue;
            if (_leyendo) return;

            _leyendo = true;
            btnIniciar.Enabled = false;

            Log("Buscando OXIMETER...");

            ulong? bluetoothAddress = await BuscarOximetroAsync();

            if (bluetoothAddress == null)
            {
                Log("No se encontró el oxímetro.");
                btnIniciar.Enabled = true;
                _leyendo = false;
                return;
            }

            Log($"Encontrado: {bluetoothAddress.Value:X}");

            _bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress.Value);

            if (_bleDevice == null)
            {
                Log("No se pudo conectar.");
                btnIniciar.Enabled = true;
                _leyendo = false;
                return;
            }

            Log($"Conectado: {_bleDevice.Name}");

            await SuscribirNotifyAsync(_bleDevice);
        }

        private Task<ulong?> BuscarOximetroAsync()
        {
            var tcs = new TaskCompletionSource<ulong?>();

            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            _watcher.Received += (sender, args) =>
            {
                string name = args.Advertisement.LocalName;

                if (name.Contains("OXIMETER", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("HealthTree", StringComparison.OrdinalIgnoreCase))
                {
                    sender.Stop();
                    tcs.TrySetResult(args.BluetoothAddress);
                }
            };

            _watcher.Start();

            Task.Delay(15000).ContinueWith(_ =>
            {
                try { _watcher?.Stop(); } catch { }
                tcs.TrySetResult(null);
            });

            return tcs.Task;
        }

        private async Task SuscribirNotifyAsync(BluetoothLEDevice bleDevice)
        {
            Guid serviceUuid =
                Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");

            GattDeviceServicesResult? servicesResult = null;

            for (int intento = 1; intento <= 15; intento++)
            {
                Log($"Leyendo servicios BLE... intento {intento}/15");

                servicesResult =
                    await bleDevice.GetGattServicesAsync(
                        BluetoothCacheMode.Cached);

                Log($"Estado servicios: {servicesResult.Status}");

                if (servicesResult.Status == GattCommunicationStatus.Success)
                    break;

                await Task.Delay(1500);
            }

            if (servicesResult == null ||
                servicesResult.Status != GattCommunicationStatus.Success)
            {
                Log("No fue posible estabilizar conexión BLE.");
                Log("Acerca notebook o usa adaptador Bluetooth USB.");
                return;
            }

            var service =
                servicesResult.Services.FirstOrDefault(
                    s => s.Uuid == serviceUuid);

            if (service == null)
            {
                Log("No se encontró servicio FFE0.");
                return;
            }

            GattCharacteristicsResult? charsResult = null;

            for (int intento = 1; intento <= 30; intento++)
            {
                Log($"Leyendo características... intento {intento}/15");

                charsResult =
                    await service.GetCharacteristicsAsync(
                        BluetoothCacheMode.Cached);

                Log($"Estado características: {charsResult.Status}");

                if (charsResult.Status == GattCommunicationStatus.Success)
                    break;

                await Task.Delay(1500);
            }

            if (charsResult == null ||
                charsResult.Status != GattCommunicationStatus.Success)
            {
                Log("No fue posible leer características BLE.");
                return;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                if (!ch.CharacteristicProperties.HasFlag(
                    GattCharacteristicProperties.Notify))
                    continue;

                ch.ValueChanged += (sender, args) =>
                {
                    byte[] data = ReadBuffer(args.CharacteristicValue);
                    ProcesarDatos(data);
                };

                Log($"Activando Notify: {ch.Uuid}");

                var status =
                    await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);

                Log($"Notify status: {status}");
            }

            Log("Lectura BLE iniciada correctamente.");
        }

        private static byte[] ReadBuffer(IBuffer buffer)
        {
            byte[] data = new byte[buffer.Length];
            DataReader.FromBuffer(buffer).ReadBytes(data);
            return data;
        }

        private void ProcesarDatos(byte[] data)
        {
            string hex = BitConverter.ToString(data);

            if (data.Length >= 6 &&
                data[0] == 0xFF &&
                data[1] == 0x44)
            {
                _ultimaLectura = DateTime.Now;

                int spo2 = data[4];
                int pulse = data[5];

                bool lecturaInvalida =
                spo2 <= 20 ||
                pulse <= 20 ||
                spo2 > 100 ||
                pulse > 220;

                string estado;

                if (lecturaInvalida)
                {
                    estado = "SIN_SENAL";
                }
                else if (spo2 >= 94)
                {
                    estado = "OK";
                }
                else if (spo2 >= 90)
                {
                    estado = "PRECAUCION";
                }
                else if (spo2 >= 85)
                {
                    estado = "ALERTA";
                }
                else
                {
                    estado = "CRITICO";
                }

                GuardarCsv(spo2, pulse, estado, hex);
                EvaluarAlertaRemota(spo2, pulse, estado);

                BeginInvoke(() =>
                {
                    lblSpo2.Text = $"SpO2: {spo2}%";
                    lblPulso.Text = $"Pulso: {pulse} bpm";

                    if (estado == "SIN_SENAL")
                    {
                        lblEstado.Text = "SIN SENAL: revisar sensor";
                        lblEstado.ForeColor = Color.Gray;

                        IniciarAlarmaRepetitiva("SIN_SENAL");

                        _inicioPrecaucion = null;
                        _inicioAlerta = null;
                    }
                    else if (estado == "OK")
                    {
                        lblEstado.Text = "Estado: OK";
                        lblEstado.ForeColor = Color.Green;

                        DetenerAlarmaRepetitiva();

                        _inicioPrecaucion = null;
                        _inicioAlerta = null;
                    }
                    else if (estado == "PRECAUCION")
                    {
                        lblEstado.Text = "PRECAUCION: SpO2 90-93%";
                        lblEstado.ForeColor = Color.Orange;

                        IniciarAlarmaRepetitiva("PRECAUCION");

                        _inicioAlerta = null;
                        _inicioPrecaucion ??= DateTime.Now;
                    }
                    else if (estado == "ALERTA")
                    {
                        lblEstado.Text = "ALERTA: SpO2 85-89%";
                        lblEstado.ForeColor = Color.Red;

                        IniciarAlarmaRepetitiva("ALERTA");

                        _inicioPrecaucion = null;
                        _inicioAlerta ??= DateTime.Now;
                    }
                    else if (estado == "CRITICO")
                    {
                        lblEstado.Text = "CRITICO: SpO2 bajo 85%";
                        lblEstado.ForeColor = Color.DarkRed;

                        IniciarAlarmaRepetitiva("CRITICO");

                        _inicioPrecaucion = null;
                        _inicioAlerta = null;
                    }

                    Log($"SpO2={spo2} Pulso={pulse} Estado={estado} HEX={hex}");
                });
            }
        }

        private void AlarmaSuave()
{
    if ((DateTime.Now - _ultimaAlarma).TotalSeconds < 15)
        return;

    _ultimaAlarma = DateTime.Now;

    Task.Run(() =>
    {
        Console.Beep(800, 300);
        Thread.Sleep(200);
        Console.Beep(800, 300);
    });
}

private void AlarmaFuerte()
{
    if ((DateTime.Now - _ultimaAlarma).TotalSeconds < 8)
        return;

    _ultimaAlarma = DateTime.Now;

    Task.Run(() =>
    {
        for (int i = 0; i < 4; i++)
        {
            Console.Beep(1200, 500);
            Thread.Sleep(250);
        }
    });
}

private void AlarmaCritica()
{
    if ((DateTime.Now - _ultimaAlarma).TotalSeconds < 3)
        return;

    _ultimaAlarma = DateTime.Now;

    Task.Run(() =>
    {
        for (int i = 0; i < 8; i++)
        {
            Console.Beep(1500, 300);
            Thread.Sleep(150);
        }
    });
}

        private void DetenerLectura()
        {
            _leyendo = false;

            btnIniciar.Enabled = true;

            try
            {
                _watcher?.Stop();
            }
            catch { }

            try
            {
                DetenerAlarmaRepetitiva();
            }
            catch { }

            try
            {
                _bleDevice?.Dispose();
            }
            catch { }

            _bleDevice = null;

            lblEstado.Text = "Lectura detenida";
            lblEstado.ForeColor = Color.Gray;

            Log("Lectura detenida.");
        }

        private void Log(string mensaje)
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => Log(mensaje));
                return;
            }

            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {mensaje}{Environment.NewLine}");
        }

        private void IniciarAlarmaRepetitiva(string tipo)
        {
            if (_alarmaActual == tipo)
                return;

            DetenerAlarmaRepetitiva();

            _alarmaActual = tipo;
            _alarmaCts = new CancellationTokenSource();
            var token = _alarmaCts.Token;

            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    if (tipo == "PRECAUCION")
                    {
                        Console.Beep(800, 300);
                        await Task.Delay(5000, token);
                    }
                    else if (tipo == "ALERTA")
                    {
                        Console.Beep(1200, 500);
                        await Task.Delay(2000, token);
                    }
                    else if (tipo == "CRITICO")
                    {
                        Console.Beep(1500, 300);
                        await Task.Delay(200, token);
                        Console.Beep(1500, 300);
                        await Task.Delay(1000, token);
                    }
                    else if (tipo == "SIN_DATOS" || tipo == "SIN_SENAL")
                    {
                        Console.Beep(500, 700);
                        await Task.Delay(300, token);
                        Console.Beep(500, 700);
                        await Task.Delay(3000, token);
                    }
                }
            }, token);
        }

        private void DetenerAlarmaRepetitiva()
        {
            try
            {
                _alarmaCts?.Cancel();
                _alarmaCts?.Dispose();
            }
            catch { }

            _alarmaCts = null;
            _alarmaActual = "";
        }

        //ALERTAS DE DISCORD
        private async Task EnviarDiscordAsync(string mensaje)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_discordWebhookUrl) ||
                    _discordWebhookUrl.Contains("PEGA_AQUI"))
                    return;

                string json = "{\"content\":\"" + mensaje.Replace("\"", "\\\"") + "\"}";

                using var content = new StringContent(json, Encoding.UTF8, "application/json");

                await _http.PostAsync(_discordWebhookUrl, content);
            }
            catch (Exception ex)
            {
                Log($"Error Discord: {ex.Message}");
            }
        }

        private void EvaluarAlertaRemota(int spo2, int pulse, string estado)
        {
            DateTime ahora = DateTime.Now;

            _ultimoEstadoAlerta = estado;

            if (estado == "OK")
                return;

            int segundosEspera = estado switch
            {
                "PRECAUCION" => 60,
                "ALERTA" => 30,
                "CRITICO" => 15,
                "SIN_SENAL" => 15,
                _ => 60
            };

            if ((ahora - _ultimoEnvioRepetido).TotalSeconds < segundosEspera)
                return;

            _ultimoEnvioRepetido = ahora;

            string mensaje = estado switch
            {
                "PRECAUCION" =>
                    $"PRECAUCION SpO2 {spo2}% | Pulso {pulse} bpm | {ahora:HH:mm:ss}",

                "ALERTA" =>
                    $"ALERTA IMPORTANTE SpO2 {spo2}% | Pulso {pulse} bpm | {ahora:HH:mm:ss}",

                "CRITICO" =>
                    $"CRITICO SpO2 {spo2}% | Pulso {pulse} bpm | {ahora:HH:mm:ss}. Revisar ahora.",

                "SIN_SENAL" =>
                    $"SIN SENAL SpO2 {spo2} | Pulso {pulse} | {ahora:HH:mm:ss}. Revisar sensor/oximetro.",

                _ =>
                    $"ALERTA {estado} SpO2 {spo2}% | Pulso {pulse} bpm | {ahora:HH:mm:ss}"
            };

            _ = EnviarDiscordAsync(mensaje);
        }

    }
}