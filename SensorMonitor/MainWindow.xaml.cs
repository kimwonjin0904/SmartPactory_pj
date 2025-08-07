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
        // 현재온도경고 
        private const double LiveTempWarningThreshold = 25.0;
        private const double StatTempWarningThreshold = 40.0;

        // 초과시 event_log.txt에 기록
        private const double AnomalyTempThreshold = 25.0;  //온도
        private const double AnomalyHumidityThreshold = 40.0; //습도

        // 설비 상태 변수
        private bool isEquipmentRunning = false;

        public ISeries[] TempSeries { get; set; }
        public ISeries[] HumSeries { get; set; }
        public List<string> Labels { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            InitCharts();
            DataContext = this;

            LogOperation("애플리케이션 시작됨");
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
                                    SaveToDatabase(timestamp, temp, hum);
                                    // TCP 수신 시 이상 징후 체크
                                    CheckAndLogAnomaly(temp, hum);
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"TCP 서버 처리 오류: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"TCP 서버 시작 오류: {ex.Message}");
            }
        }

        // 데이터베이스에 센서 데이터를 저장하는 메서드
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
                Console.WriteLine($"DB 저장 오류: {ex.Message}");
            }
        }

        // UI 데이터를 새로고침하는 타이머 시작
        private void StartRefreshTimer()
        {
            _refreshTimer = new System.Timers.Timer(2000);
            _refreshTimer.Elapsed += (s, e) => Dispatcher.Invoke(() => {
                LoadSensorStatistics();
                LoadRecentSensorData();
            });
            _refreshTimer.Start();
        }

        // UI 데이터를 새로고침하는 타이머 중지
        private void StopRefreshTimer()
        {
            _refreshTimer?.Stop();
        }

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
                            MinTempText.Text = $"최소 온도: {(reader.IsDBNull(reader.GetOrdinal("MIN_TEMP")) ? "N/A" : $"{reader.GetDouble(reader.GetOrdinal("MIN_TEMP")):F1}°C")}";
                            MaxTempText.Text = $"최대 온도: {(reader.IsDBNull(reader.GetOrdinal("MAX_TEMP")) ? "N/A" : $"{reader.GetDouble(reader.GetOrdinal("MAX_TEMP")):F1}°C")}";
                            AvgTempText.Text = $"평균 온도: {(reader.IsDBNull(reader.GetOrdinal("AVG_TEMP")) ? "N/A" : $"{reader.GetDouble(reader.GetOrdinal("AVG_TEMP")):F1}°C")}";
                            MinHumText.Text = $"최소 습도: {(reader.IsDBNull(reader.GetOrdinal("MIN_HUM")) ? "N/A" : $"{reader.GetDouble(reader.GetOrdinal("MIN_HUM")):F1}%")}";
                            MaxHumText.Text = $"최대 습도: {(reader.IsDBNull(reader.GetOrdinal("MAX_HUM")) ? "N/A" : $"{reader.GetDouble(reader.GetOrdinal("MAX_HUM")):F1}%")}";
                            AvgHumText.Text = $"평균 습도: {(reader.IsDBNull(reader.GetOrdinal("AVG_HUM")) ? "N/A" : $"{reader.GetDouble(reader.GetOrdinal("AVG_HUM")):F1}%")}";

                            if (!reader.IsDBNull(reader.GetOrdinal("MAX_TEMP")) && reader.GetDouble(reader.GetOrdinal("MAX_TEMP")) > StatTempWarningThreshold)
                            {
                                WarningTextBlock.Text = $"🔥 통계상 최고 온도 경고: {reader.GetDouble(reader.GetOrdinal("MAX_TEMP")):F1}°C";
                                WarningTextBlock.Foreground = Brushes.Red;
                                WarningTextBlock.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                if (WarningTextBlock.Text.StartsWith("🔥 통계상 최고 온도 경고"))
                                {
                                    WarningTextBlock.Visibility = Visibility.Collapsed;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"통계 데이터 로드 오류 발생: {ex.Message}");
            }
        }

        private void LoadRecentSensorData()
        {
            try
            {
                using (var conn = new OracleConnection(_connStr))
                {
                    conn.Open();

                    string recentDataQuery = "SELECT * " +
                        "FROM (SELECT * FROM SENSOR_DATA ORDER BY ID DESC) " +
                        "WHERE ROWNUM <= 10 ORDER BY ID ASC";
                    var adapter = new OracleDataAdapter(recentDataQuery, conn);
                    var dtRecent = new DataTable();
                    adapter.Fill(dtRecent);

                    SensorDataGrid.ItemsSource = dtRecent.DefaultView;

                    if (dtRecent.Rows.Count > 0)
                    {
                        var latest = dtRecent.Rows[dtRecent.Rows.Count - 1];
                        double latestTemp = (latest["TEMPERATURE"] == DBNull.Value) ? 0.0 : Convert.ToDouble(latest["TEMPERATURE"]);
                        double latestHum = (latest["HUMIDITY"] == DBNull.Value) ? 0.0 : Convert.ToDouble(latest["HUMIDITY"]);
                        string latestTimestamp = (latest["TIMESTAMP"] == DBNull.Value) ? "N/A" : Convert.ToDateTime(latest["TIMESTAMP"]).ToString("yyyy-MM-dd HH:mm:ss");

                        LatestValueText.Text = $"⏱️ {latestTimestamp} | 🌡️ {latestTemp:F1}°C | 💧 {latestHum:F1}%";

                        if (latestTemp > LiveTempWarningThreshold)
                        {
                            WarningTextBlock.Text = $"🔥 현재 온도 경고: {latestTemp:F1}°C";
                            WarningTextBlock.Foreground = Brushes.Red;
                            WarningTextBlock.Visibility = Visibility.Visible;
                        }
                    }

                    var tempValues = new List<double>();
                    var humValues = new List<double>();
                    var labels = new List<string>();

                    foreach (DataRow row in dtRecent.Rows)
                    {
                        if (row["TEMPERATURE"] != DBNull.Value)
                        {
                            tempValues.Add(Convert.ToDouble(row["TEMPERATURE"]));
                        }
                        if (row["HUMIDITY"] != DBNull.Value)
                        {
                            humValues.Add(Convert.ToDouble(row["HUMIDITY"]));
                        }
                        if (row["TIMESTAMP"] != DBNull.Value)
                        {
                            labels.Add(Convert.ToDateTime(row["TIMESTAMP"]).ToString("HH:mm:ss"));
                        }
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
                MessageBox.Show($"데이터 로드 오류 발생: {ex.Message}");
            }
        }

        
        // 추가 기능들
        // 가동/정지 버튼 클릭 이벤트 핸들러
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

        // 센서 이상 징후를 감지하고 기록
        private void CheckAndLogAnomaly(double temp, double hum)
        {
            if (temp > AnomalyTempThreshold)
            {
                string message = $"온도 이상 징후 감지: {temp:F1}°C (임계값: {AnomalyTempThreshold}°C)";
                LogEvent(message);
            }

            if (hum > AnomalyHumidityThreshold)
            {
                string message = $"습도 이상 징후 감지: {hum:F1}% (임계값: {AnomalyHumidityThreshold}%)";
                LogEvent(message);
            }
        }

        // 공통 이벤트 기록 (파일 및 ListBox) 기능 추가 한거
        private void LogEvent(string message)
        {
            try
            {
                string logFilePath = "event_log.txt";
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [이상 징후] {message}\n";
                File.AppendAllText(logFilePath, logMessage);

                // UI로그 추가
                EventLogListBox.Items.Add($"[이상 징후] {message}");
                EventLogListBox.ScrollIntoView(EventLogListBox.Items[EventLogListBox.Items.Count - 1]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"센서 이상 로그 저장 오류: {ex.Message}");
            }
        }

        // 작업 이력을 기록 (운영일지) 기능한거
        private void LogOperation(string message)
        {
            try
            {
                string logFilePath = "operation_log.txt";
                string logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - [운영 기록] {message}\n";
                File.AppendAllText(logFilePath, logMessage);

                // UI로그 추가
                EventLogListBox.Items.Add($"[운영 기록] {message}");
                EventLogListBox.ScrollIntoView(EventLogListBox.Items[EventLogListBox.Items.Count - 1]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"운영 로그 저장 오류: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        // 애플리케이션 종료 시 로그 기록
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            LogOperation("애플리케이션 종료됨");
            _server?.Stop(); 
        }
    }
}
