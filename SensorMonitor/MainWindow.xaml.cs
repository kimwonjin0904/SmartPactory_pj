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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Media;

namespace SensorMonitorApp
{//잔디 심어졌는지 확인
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private TcpListener _server;
        private System.Timers.Timer _refreshTimer;
        private string _connStr = "User Id=kwj;Password=kwj;Data Source=192.168.25.6:1521/xe";
        private const double LiveTempWarningThreshold = 24.0;
        private const double StatTempWarningThreshold = 30.0;

        public ISeries[] TempSeries { get; set; }
        public ISeries[] HumSeries { get; set; }
        public List<string> Labels { get; set; }

        public MainWindow()
        {
            InitializeComponent();
            InitCharts();
            DataContext = this;

            LoadSensorStatistics();
            StartTcpServer();
            StartTimer();
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

                                var jsonDoc = JsonDocument.Parse(received);
                                var root = jsonDoc.RootElement;
                                double temp = root.GetProperty("temperature").GetDouble();
                                double hum = root.GetProperty("humidity").GetDouble();
                                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                                SaveToDatabase(timestamp, temp, hum);
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

        private void StartTimer()
        {
            _refreshTimer = new System.Timers.Timer(2000);
            _refreshTimer.Elapsed += (s, e) => Dispatcher.Invoke(() => {
                LoadSensorStatistics();
                LoadRecentSensorData();
            });
            _refreshTimer.Start();
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

                    string recentDataQuery = "SELECT * FROM (SELECT * FROM SENSOR_DATA ORDER BY ID DESC) WHERE ROWNUM <= 10 ORDER BY ID ASC";
                    var adapter = new OracleDataAdapter(recentDataQuery, conn);
                    var dtRecent = new DataTable();
                    adapter.Fill(dtRecent);

                    SensorDataGrid.ItemsSource = dtRecent.DefaultView;

                    if (dtRecent.Rows.Count > 0)
                    {
                        var latest = dtRecent.Rows[dtRecent.Rows.Count - 1];
                        double latestTemp = Convert.ToDouble(latest["TEMPERATURE"]);
                        double latestHum = Convert.ToDouble(latest["HUMIDITY"]);
                        string latestTimestamp = Convert.ToDateTime(latest["TIMESTAMP"]).ToString("yyyy-MM-dd HH:mm:ss");

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
                MessageBox.Show($"데이터 로드 오류 발생: {ex.Message}");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}