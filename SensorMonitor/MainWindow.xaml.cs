using System;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Oracle.ManagedDataAccess.Client;
using System.Timers;

namespace SensorMonitorApp
{
    public partial class MainWindow : Window
    {
        private TcpListener _server;
        private System.Timers.Timer _refreshTimer;
        private string _connStr = "User Id=kwj;Password=kwj;Data Source=192.168.25.46:1521/xe";

        private const double TempWarningThreshold = 25.0;  // 온도 임계값

        public MainWindow()
        {
            InitializeComponent();
            StartTcpServer();  // TCP 서버 실행
            StartTimer();      // DB 조회 타이머 실행
        }

        private void StartTcpServer()
        {
            _server = new TcpListener(IPAddress.Any, 9999);
            _server.Start();
            Console.WriteLine("[TCP Server] 9999 포트 리스닝 시작");

            Task.Run(() =>
            {
                while (true)
                {
                    try
                    {
                        using (TcpClient client = _server.AcceptTcpClient())
                        using (NetworkStream stream = client.GetStream())
                        {
                            byte[] buffer = new byte[1024];
                            int bytesRead = stream.Read(buffer, 0, buffer.Length);
                            string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                            Console.WriteLine($"[TCP Server] 수신 데이터: {received}");

                            // JSON 파싱
                            var jsonDoc = JsonDocument.Parse(received);
                            var root = jsonDoc.RootElement;
                            double temp = root.GetProperty("temperature").GetDouble();
                            double hum = root.GetProperty("humidity").GetDouble();
                            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                            // DB 저장
                            SaveToDatabase(timestamp, temp, hum);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[TCP Server] 오류: " + ex.Message);
                    }
                }
            });
        }

        private void SaveToDatabase(string timestamp, double temp, double hum)
        {
            try
            {
                using (var conn = new OracleConnection(_connStr))
                {
                    conn.Open();

                    var cmd = new OracleCommand(
                        "INSERT INTO SENSOR_DATA (TIMESTAMP, TEMPERATURE, HUMIDITY) VALUES (:1, :2, :3)", conn);
                    cmd.Parameters.Add(new OracleParameter("1", timestamp));
                    cmd.Parameters.Add(new OracleParameter("2", temp));
                    cmd.Parameters.Add(new OracleParameter("3", hum));
                    cmd.ExecuteNonQuery();

                    conn.Commit();

                    Console.WriteLine("→ Oracle DB 저장 완료");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DB 오류] 데이터 저장 실패: " + ex.Message);
            }
        }

        private void StartTimer()
        {
            _refreshTimer = new System.Timers.Timer(2000); // 2초마다
            _refreshTimer.Elapsed += (s, e) => Dispatcher.Invoke(LoadSensorData);
            _refreshTimer.Start();
        }

        private void LoadSensorData()
        {
            try
            {
                using (var conn = new OracleConnection(_connStr))
                {
                    conn.Open();
                    string query = "SELECT * FROM (SELECT * FROM SENSOR_DATA ORDER BY ID DESC) WHERE ROWNUM <= 10 ORDER BY ID DESC";

                    var cmd = new OracleCommand(query, conn);
                    var adapter = new OracleDataAdapter(cmd);
                    var dt = new DataTable();
                    adapter.Fill(dt);

                    SensorDataGrid.ItemsSource = dt.DefaultView;

                    if (dt.Rows.Count > 0)
                    {
                        var latest = dt.Rows[0];
                        double latestTemp = Convert.ToDouble(latest["TEMPERATURE"]);

                        LatestValueText.Text = $"⏱️ {latest["TIMESTAMP"]} | 🌡️ {latestTemp}°C | 💧 {latest["HUMIDITY"]}%";

                        // 온도 임계값 체크 및 경고 메시지 출력
                        if (latestTemp > TempWarningThreshold)
                        {
                            WarningTextBlock.Text = $"🔥 온도 과다! 현재 온도: {latestTemp}°C";
                            WarningTextBlock.Foreground = Brushes.Red;
                        }
                        else
                        {
                            WarningTextBlock.Text = string.Empty;
                        }
                    }
                    else
                    {
                        WarningTextBlock.Text = string.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                LatestValueText.Text = $"[오류] DB 조회 실패: {ex.Message}";
                WarningTextBlock.Text = string.Empty;
            }
        }
    }
}
