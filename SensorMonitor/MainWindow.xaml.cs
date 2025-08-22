using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WPF;
using Oracle.ManagedDataAccess.Client;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media;

namespace SensorMonitorApp
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private TcpListener _server;
        private System.Timers.Timer _refreshTimer;
        private string _connStr = "User Id=kwj;Password=kwj;Data Source=192.168.25.47:1521/xe";

        //임계값 정의
        private const double LiveTempWarningThreshold = 25.0;
        private const double StatTempWarningThreshold = 40.0;
        private const double AnomalyTempThreshold = 25.0;   // 이상 징후 감지용 온도
        private const double AnomalyHumidityThreshold = 40.0; // 이상 징후 감지용 습도

        // 설비 상태 나타낸거
        private bool isEquipmentRunning = false;

        // 차트 바인딩 데이터
        public ISeries[] TempSeries { get; set; }
        public ISeries[] HumSeries { get; set; }
        public List<string> Labels { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            InitCharts();
            DataContext = this;

            LogOperation("애플리케이션 시작됨"); // 운영일지 시작 기록
            StartTcpServer();
        }

        private void InitCharts()
        {
            TempSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = new List<double>(),
                    Fill = null,
                    Stroke = new SolidColorPaint { Color = SKColors.Red, StrokeThickness = 2 }
                }
            };

            HumSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Values = new List<double>(),
                    Fill = null,
                    Stroke = new SolidColorPaint { Color = SKColors.SkyBlue, StrokeThickness = 2 }
                }
            };

            Labels = new List<string>();
        }

        // TCP 서버 (EIF/ECS 역할: 설비 ↔ MES 인터페이스)
        private void StartTcpServer()
        {
            try
            {
                _server = new TcpListener(IPAddress.Any, 9999);
                _server.Start();
                Task.Run(async () =>
                {
                    while (true)
                    {
                        try
                        {
                            using (TcpClient client = await _server.AcceptTcpClientAsync())
                            using (NetworkStream stream = client.GetStream())
                            {
                                byte[] buffer = new byte[1024];
                                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                                string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                                var jsonDoc = JsonDocument.Parse(received);
                                var root = jsonDoc.RootElement;
                                double temp = root.GetProperty("temperature").GetDouble();
                                double hum = root.GetProperty("humidity").GetDouble();
                                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                                Dispatcher.Invoke(() =>
                                {
                                    if (isEquipmentRunning) // ✅ 설비 가동 중일 때만 데이터 반영
                                    {
                                        SaveToDatabase(timestamp, temp, hum);
                                        CheckAndLogAnomaly(temp, hum);
                                    }
                                    else
                                    {
                                        LogOperation("데이터 수신됨 (설비 정지 상태로 미반영)");
                                    }
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            LogEvent($"TCP 서버 처리 오류: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"TCP 서버 시작 오류: {ex.Message}");
            }
        }

        // DB 저장 (MES 기능: 생산실적 기록)
        private void SaveToDatabase(string timestamp, double temp, double hum)
        {
            try
            {
                using (var conn = new OracleConnection(_connStr))
                {
                    conn.Open();
                    var cmd = new OracleCommand(
                        "INSERT INTO SENSOR_DATA (TIMESTAMP, TEMPERATURE, HUMIDITY) VALUES (:timestamp, :temp, :hum)", conn);
                    cmd.Parameters.Add(new OracleParameter("timestamp", timestamp));
                    cmd.Parameters.Add(new OracleParameter("temp", temp));
                    cmd.Parameters.Add(new OracleParameter("hum", hum));
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                LogEvent($"DB 저장 오류: {ex.Message}");
            }
        }

        // UI 새로고침 타이머
        private void StartRefreshTimer()
        {
            _refreshTimer = new System.Timers.Timer(2000);
            _refreshTimer.Elapsed += (s, e) => Dispatcher.Invoke(() => {
                LoadSensorStatistics();
                LoadRecentSensorData();
            });
            _refreshTimer.Start();
        }

        private void StopRefreshTimer() => _refreshTimer?.Stop();

        // 통계 데이터 조회
        private void LoadSensorStatistics()
        {
            try
            {
                using (var conn = new OracleConnection(_connStr))
                {
                    conn.Open();
                    string sql = @"
                        SELECT
                            MIN(TEMPERATURE) AS MIN_TEMP,
                            MAX(TEMPERATURE) AS MAX_TEMP,
                            AVG(TEMPERATURE) AS AVG_TEMP,
                            MIN(HUMIDITY) AS MIN_HUM,
                            MAX(HUMIDITY) AS MAX_HUM,
                            AVG(HUMIDITY) AS AVG_HUM
                        FROM SENSOR_DATA";

                    using (OracleCommand cmd = new OracleCommand(sql, conn))
                    using (OracleDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            MinTempText.Text = $"최소 온도: {GetValueOrNA(reader, "MIN_TEMP")}°C";
                            MaxTempText.Text = $"최대 온도: {GetValueOrNA(reader, "MAX_TEMP")}°C";
                            AvgTempText.Text = $"평균 온도: {GetValueOrNA(reader, "AVG_TEMP")}°C";
                            MinHumText.Text = $"최소 습도: {GetValueOrNA(reader, "MIN_HUM")}%";
                            MaxHumText.Text = $"최대 습도: {GetValueOrNA(reader, "MAX_HUM")}%";
                            AvgHumText.Text = $"평균 습도: {GetValueOrNA(reader, "AVG_HUM")}%";

                            if (!reader.IsDBNull(reader.GetOrdinal("MAX_TEMP")) &&
                                reader.GetDouble(reader.GetOrdinal("MAX_TEMP")) > StatTempWarningThreshold)
                            {
                                WarningTextBlock.Text = $"🔥 통계상 최고 온도 경고";
                                WarningTextBlock.Foreground = Brushes.Red;
                                WarningTextBlock.Visibility = Visibility.Visible;
                                LogEvent("통계상 온도 초과 경고 발생");
                            }
                            else
                            {
                                WarningTextBlock.Visibility = Visibility.Collapsed;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogEvent($"통계 데이터 로드 오류: {ex.Message}");
            }
        }

        private string GetValueOrNA(OracleDataReader reader, string col)
            => reader.IsDBNull(reader.GetOrdinal(col)) ? "N/A" : reader.GetDouble(reader.GetOrdinal(col)).ToString("F1");

        // 최근 센서 데이터
        private void LoadRecentSensorData()
        {
            try
            {
                using (var conn = new OracleConnection(_connStr))
                {
                    conn.Open();
                    string sql = "SELECT * FROM (SELECT * FROM SENSOR_DATA ORDER BY ID DESC) WHERE ROWNUM <= 10 ORDER BY ID ASC";
                    var adapter = new OracleDataAdapter(sql, conn);
                    var dtRecent = new DataTable();
                    adapter.Fill(dtRecent);

                    SensorDataGrid.ItemsSource = dtRecent.DefaultView;

                    if (dtRecent.Rows.Count > 0)
                    {
                   
                        var latest = dtRecent.Rows[dtRecent.Rows.Count - 1];
                        double latestTemp = Convert.ToDouble(latest["TEMPERATURE"]);
                        double latestHum = Convert.ToDouble(latest["HUMIDITY"]);
                        string latestTimestamp = Convert.ToDateTime(latest["TIMESTAMP"]).ToString("yyyy-MM-dd HH:mm:ss");

                        LatestValueText.Text = $"⏱ {latestTimestamp} | 🌡 {latestTemp:F1}°C | 💧 {latestHum:F1}%";

                        if (latestTemp > LiveTempWarningThreshold)
                        {
                            WarningTextBlock.Text = $"🔥 현재 온도 경고: {latestTemp:F1}°C";
                            WarningTextBlock.Visibility = Visibility.Visible;
                            LogEvent("실시간 온도 초과 경고 발생");
                        }
                    }

                    var tempValues = new List<double>();
                    var humValues = new List<double>();
                    var labels = new List<string>();
                    foreach (DataRow row in dtRecent.Rows)
                    {
                        tempValues.Add(Convert.ToDouble(row["TEMPERATURE"]));
                        humValues.Add(Convert.ToDouble(row["HUMIDITY"]));
                        labels.Add(Convert.ToDateTime(row["TIMESTAMP"]).ToString("HH:mm:ss"));
                    }
                    ((LineSeries<double>)TempSeries[0]).Values = tempValues;
                    ((LineSeries<double>)HumSeries[0]).Values = humValues;
                    Labels = labels;

                    OnPropertyChanged(nameof(TempSeries));
                    OnPropertyChanged(nameof(HumSeries));
                    OnPropertyChanged(nameof(Labels));
                }
            }
            catch (Exception ex)
            {
                LogEvent($"최근 데이터 로드 오류: {ex.Message}");
            }
        }

        // 설비 가동/정지 버튼
        private void ToggleEquipmentStatusButton_Click(object sender, RoutedEventArgs e)
        {
            isEquipmentRunning = !isEquipmentRunning;
            if (isEquipmentRunning)
            {
                EquipmentStatusText.Text = "가동";
                EquipmentStatusText.Foreground = Brushes.Green;
                LogOperation("설비 가동 시작");
                StartRefreshTimer();
            }
            else
            {
                EquipmentStatusText.Text = "정지";
                EquipmentStatusText.Foreground = Brushes.Red;
                LogOperation("설비 정지");
                StopRefreshTimer();
            }
        }

        // 이상 징후 탐지
        private void CheckAndLogAnomaly(double temp, double hum)
        {
            if (temp > AnomalyTempThreshold)
                LogEvent($"온도 이상: {temp:F1}°C");
            if (hum > AnomalyHumidityThreshold)
                LogEvent($"습도 이상: {hum:F1}%");
        }

        // 이벤트 로그 기록
        private void LogEvent(string message)
        {
            string logFilePath = "event_log.txt";
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [이상/알람] {message}\n";
            File.AppendAllText(logFilePath, logMessage);

            EventLogListBox.Items.Add($"[이상/알람] {message}");
            
            EventLogListBox.ScrollIntoView(EventLogListBox.Items[EventLogListBox.Items.Count - 1]);
        }

        // 운영일지 기록
        private void LogOperation(string message)
        {
            string logFilePath = "operation_log.txt";
            string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [운영] {message}\n";
            File.AppendAllText(logFilePath, logMessage);

            EventLogListBox.Items.Add($"[운영] {message}");
          
            EventLogListBox.ScrollIntoView(EventLogListBox.Items[EventLogListBox.Items.Count - 1]);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            LogOperation("애플리케이션 종료됨");
            _server?.Stop();
        }
    }
}
