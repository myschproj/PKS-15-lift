using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Newtonsoft.Json;
using OxyPlot;
using OxyPlot.Series;

namespace ElevatorSimulator
{
    public partial class MainWindow : Window
    {
        // Серверные компоненты
        private HttpListener _httpListener;
        private CancellationTokenSource _cts;
        private readonly int _defaultPort = 8080;
        private bool _isServerRunning = false;

        // Клиент
        private readonly HttpClient _httpClient = new HttpClient();

        // Мониторинг
        private readonly MonitoringService _monitoring = new MonitoringService();
        private readonly Logger _logger = new Logger();

        // Симулятор лифтов
        private Building _building = new Building();
        private DispatcherTimer _animationTimer;
        private readonly Dictionary<int, Rectangle> _elevatorRectangles = new();
        private const int FloorHeight = 50;
        private const int ElevatorWidth = 30;
        private const int ElevatorSpacing = 40;

        public MainWindow()
        {
            InitializeComponent();
            _logger.OnLogAdded += (msg) => Dispatcher.Invoke(() => ServerLogTextBox.AppendText(msg + Environment.NewLine));
            _monitoring.PropertyChanged += Monitoring_PropertyChanged;
            _monitoring.Logs.CollectionChanged += (s, e) => Dispatcher.Invoke(UpdateLogsDataGrid);

            // Привязка данных для мониторинга
            LogsDataGrid.ItemsSource = _monitoring.FilteredLogs;
            LoadPlotView.Model = _monitoring.LoadPlotModel;

            // Инициализация симулятора
            InitializeBuildingVisualization();
            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _animationTimer.Tick += AnimationTimer_Tick;
            _animationTimer.Start();

            // Кнопки вызова этажей
            var floorItems = new List<FloorButtonViewModel>();
            for (int i = 1; i <= _building.FloorsCount; i++)
            {
                var vm = new FloorButtonViewModel { FloorNumber = i };
                vm.CallElevatorCommand = new RelayCommand<int>(floor => CallElevator(floor));
                floorItems.Add(vm);
            }
            FloorButtonsItemsControl.ItemsSource = floorItems;
        }

        #region Сервер
        private async void StartServerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(ServerPortTextBox.Text, out int port))
                port = _defaultPort;

            try
            {
                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://+:{port}/");
                _httpListener.Start();
                _isServerRunning = true;
                _cts = new CancellationTokenSource();

                StartServerButton.IsEnabled = false;
                StopServerButton.IsEnabled = true;
                ServerLogTextBox.AppendText($"Сервер запущен на порту {port}{Environment.NewLine}");

                // Запуск обработки входящих запросов
                _ = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        try
                        {
                            var context = await _httpListener.GetContextAsync();
                            _ = Task.Run(() => ProcessRequestAsync(context));
                        }
                        catch (HttpListenerException) { break; }
                        catch (Exception ex)
                        {
                            _logger.LogError($"Ошибка приёма запроса: {ex.Message}");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось запустить сервер: {ex.Message}\nВозможно, требуются права администратора.", "Ошибка");
            }
        }

        private void StopServerButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _httpListener?.Stop();
            _httpListener?.Close();
            _isServerRunning = false;
            StartServerButton.IsEnabled = true;
            StopServerButton.IsEnabled = false;
            ServerLogTextBox.AppendText("Сервер остановлен.\n");
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var sw = Stopwatch.StartNew();

            string requestBody = "";
            if (request.HasEntityBody)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                requestBody = await reader.ReadToEndAsync();
            }

            _logger.LogRequest(request.HttpMethod, request.Url.ToString(), request.Headers, requestBody);

            string responseText = "";
            int statusCode = 200;

            try
            {
                if (request.HttpMethod == "GET")
                {
                    if (request.Url.AbsolutePath == "/status")
                        responseText = GetServerStatus();
                    else if (request.Url.AbsolutePath == "/elevators")
                        responseText = JsonConvert.SerializeObject(_building.Elevators);
                    else
                        statusCode = 404;
                }
                else if (request.HttpMethod == "POST")
                {
                    if (request.Url.AbsolutePath == "/message")
                        responseText = HandleMessage(requestBody);
                    else if (request.Url.AbsolutePath == "/call")
                        responseText = HandleCall(requestBody);
                    else
                        statusCode = 404;
                }
                else
                    statusCode = 405;
            }
            catch (Exception ex)
            {
                statusCode = 500;
                responseText = JsonConvert.SerializeObject(new { error = ex.Message });
            }

            sw.Stop();
            _monitoring.RecordRequest(request.HttpMethod, sw.ElapsedMilliseconds, statusCode);

            response.StatusCode = statusCode;
            byte[] buffer = Encoding.UTF8.GetBytes(responseText);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private string GetServerStatus()
        {
            var status = new
            {
                uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(),
                totalRequests = _monitoring.GetCount + _monitoring.PostCount,
                getCount = _monitoring.GetCount,
                postCount = _monitoring.PostCount,
                avgGetTime = _monitoring.AvgGetTime,
                avgPostTime = _monitoring.AvgPostTime
            };
            return JsonConvert.SerializeObject(status);
        }

        private string HandleMessage(string json)
        {
            var msg = JsonConvert.DeserializeObject<MessageRequest>(json);
            var id = Guid.NewGuid().ToString();
            MessageStorage.Messages.Add(new SavedMessage { Id = id, Text = msg.Message });
            return JsonConvert.SerializeObject(new { id });
        }

        private string HandleCall(string json)
        {
            var call = JsonConvert.DeserializeObject<CallRequest>(json);
            _building.AddCall(call.Floor, call.Direction);
            return JsonConvert.SerializeObject(new { status = "accepted" });
        }
        #endregion

        #region Клиент
        private async void SendRequestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var method = ((ComboBoxItem)MethodComboBox.SelectedItem).Content.ToString();
                var url = ClientUrlTextBox.Text;
                var body = RequestBodyTextBox.Text;

                var request = new HttpRequestMessage(method == "GET" ? HttpMethod.Get : HttpMethod.Post, url);
                if (method == "POST" && !string.IsNullOrWhiteSpace(body))
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                _logger.LogClientRequest(method, url, body);
                var response = await _httpClient.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                ResponseTextBox.Text = $"Статус: {(int)response.StatusCode} {response.StatusCode}\n{responseBody}";
                _logger.LogClientResponse(response.StatusCode, responseBody);
            }
            catch (Exception ex)
            {
                ResponseTextBox.Text = $"Ошибка: {ex.Message}";
            }
        }
        #endregion

        #region Мониторинг UI
        private void Monitoring_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatsTextBlock.Text = $"GET: {_monitoring.GetCount} | POST: {_monitoring.PostCount} | " +
                                      $"Среднее GET: {_monitoring.AvgGetTime:F1} ms | Среднее POST: {_monitoring.AvgPostTime:F1} ms";
            });
        }

        private void UpdateLogsDataGrid()
        {
            LogsDataGrid.ItemsSource = null;
            LogsDataGrid.ItemsSource = _monitoring.FilteredLogs;
        }

        private void FilterMethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void FilterStatusComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            string methodFilter = ((ComboBoxItem)FilterMethodComboBox.SelectedItem)?.Content.ToString();
            string statusFilter = ((ComboBoxItem)FilterStatusComboBox.SelectedItem)?.Content.ToString();

            _monitoring.Filter(methodFilter == "Все" ? null : methodFilter,
                               statusFilter == "Все" ? null : (statusFilter == "200 OK" ? 200 : 400));
        }
        #endregion

        #region Симулятор лифтов
        private void InitializeBuildingVisualization()
        {
            BuildingCanvas.Children.Clear();
            _elevatorRectangles.Clear();

            // Рисуем этажи
            for (int floor = 1; floor <= _building.FloorsCount; floor++)
            {
                var y = BuildingCanvas.Height - (floor * FloorHeight);
                var line = new Line
                {
                    X1 = 0, Y1 = y,
                    X2 = BuildingCanvas.Width, Y2 = y,
                    Stroke = Brushes.LightGray,
                    StrokeThickness = 1
                };
                BuildingCanvas.Children.Add(line);

                var label = new TextBlock
                {
                    Text = floor.ToString(),
                    Margin = new Thickness(5, y - 15, 0, 0)
                };
                BuildingCanvas.Children.Add(label);
            }

            // Создаём прямоугольники лифтов
            for (int i = 0; i < _building.Elevators.Count; i++)
            {
                var rect = new Rectangle
                {
                    Width = ElevatorWidth,
                    Height = FloorHeight - 4,
                    Fill = Brushes.SteelBlue,
                    Stroke = Brushes.Black,
                    StrokeThickness = 1
                };
                var x = 100 + i * (ElevatorWidth + ElevatorSpacing);
                Canvas.SetLeft(rect, x);
                BuildingCanvas.Children.Add(rect);
                _elevatorRectangles[_building.Elevators[i].Id] = rect;
                UpdateElevatorPosition(_building.Elevators[i]);
            }
        }

        private void UpdateElevatorPosition(Elevator elevator)
        {
            if (_elevatorRectangles.TryGetValue(elevator.Id, out var rect))
            {
                double y = BuildingCanvas.Height - (elevator.CurrentFloor * FloorHeight) + 2;
                Canvas.SetTop(rect, y);
            }
        }

        private void AnimationTimer_Tick(object sender, EventArgs e)
        {
            _building.Update();
            foreach (var elevator in _building.Elevators)
                UpdateElevatorPosition(elevator);
        }

        private void CallElevator(int floor)
        {
            // Вызов лифта на этаж (пока просто вверх)
            _building.AddCall(floor, Direction.Up);
        }
        #endregion

        #region Вспомогательные классы и модели
        public class Logger
        {
            private readonly string _logFilePath = "logs.txt";
            public event Action<string> OnLogAdded;

            public void LogRequest(string method, string url, System.Collections.Specialized.NameValueCollection headers, string body)
            {
                var msg = $"[{DateTime.Now:HH:mm:ss}] REQUEST: {method} {url}\nHeaders: {headers}\nBody: {body}";
                AppendLog(msg);
            }

            public void LogError(string error)
            {
                AppendLog($"[ERROR] {error}");
            }

            public void LogClientRequest(string method, string url, string body)
            {
                AppendLog($"[CLIENT] {method} {url} Body: {body}");
            }

            public void LogClientResponse(System.Net.HttpStatusCode status, string body)
            {
                AppendLog($"[CLIENT] Response: {status} Body: {body}");
            }

            private void AppendLog(string message)
            {
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
                OnLogAdded?.Invoke(message);
            }
        }

        public class MonitoringService : INotifyPropertyChanged
        {
            private int _getCount;
            private int _postCount;
            private long _totalGetTime;
            private long _totalPostTime;

            public ObservableCollection<RequestLogEntry> Logs { get; } = new();
            public ObservableCollection<RequestLogEntry> FilteredLogs { get; } = new();

            public int GetCount => _getCount;
            public int PostCount => _postCount;
            public double AvgGetTime => _getCount == 0 ? 0 : _totalGetTime / (double)_getCount;
            public double AvgPostTime => _postCount == 0 ? 0 : _totalPostTime / (double)_postCount;

            public PlotModel LoadPlotModel { get; }

            private LineSeries _loadSeries;
            private readonly ConcurrentQueue<DateTime> _requestTimestamps = new();

            public event PropertyChangedEventHandler PropertyChanged;

            public MonitoringService()
            {
                LoadPlotModel = new PlotModel { Title = "Нагрузка (запросов/мин)" };
                _loadSeries = new LineSeries { Title = "Requests" };
                LoadPlotModel.Series.Add(_loadSeries);
            }

            public void RecordRequest(string method, long elapsedMs, int statusCode)
            {
                if (method == "GET") { _getCount++; _totalGetTime += elapsedMs; }
                else if (method == "POST") { _postCount++; _totalPostTime += elapsedMs; }

                var entry = new RequestLogEntry
                {
                    Timestamp = DateTime.Now,
                    Method = method,
                    StatusCode = statusCode,
                    Duration = elapsedMs
                };
                Logs.Add(entry);
                _requestTimestamps.Enqueue(entry.Timestamp);

                // Обновление графика каждые ~10 записей или по таймеру
                if (Logs.Count % 10 == 0)
                    UpdatePlot();

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GetCount)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PostCount)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvgGetTime)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvgPostTime)));
            }

            private void UpdatePlot()
            {
                var now = DateTime.Now;
                var minuteBuckets = Logs.Where(l => l.Timestamp > now.AddMinutes(-5))
                                        .GroupBy(l => new DateTime(l.Timestamp.Year, l.Timestamp.Month, l.Timestamp.Day,
                                                                  l.Timestamp.Hour, l.Timestamp.Minute, 0))
                                        .Select(g => new { Time = g.Key, Count = g.Count() });

                _loadSeries.Points.Clear();
                foreach (var bucket in minuteBuckets.OrderBy(b => b.Time))
                    _loadSeries.Points.Add(new DataPoint(DateTimeAxis.ToDouble(bucket.Time), bucket.Count));
                LoadPlotModel.InvalidatePlot(true);
            }

            public void Filter(string method, int? statusThreshold)
            {
                FilteredLogs.Clear();
                var query = Logs.AsEnumerable();
                if (!string.IsNullOrEmpty(method))
                    query = query.Where(l => l.Method == method);
                if (statusThreshold.HasValue)
                {
                    if (statusThreshold == 200)
                        query = query.Where(l => l.StatusCode >= 200 && l.StatusCode < 300);
                    else
                        query = query.Where(l => l.StatusCode >= 400);
                }
                foreach (var entry in query.OrderByDescending(l => l.Timestamp))
                    FilteredLogs.Add(entry);
            }
        }

        public class RequestLogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Method { get; set; }
            public int StatusCode { get; set; }
            public long Duration { get; set; }
        }

        public class Elevator
        {
            public int Id { get; set; }
            public int CurrentFloor { get; set; } = 1;
            public bool IsMoving { get; set; }
            public Direction CurrentDirection { get; set; } = Direction.Idle;
            public List<int> TargetFloors { get; set; } = new();
            public int Capacity { get; set; } = 8;
            public int CurrentLoad { get; set; } = 0;
        }

        public enum Direction { Up, Down, Idle }

        public class Building
        {
            public int FloorsCount { get; set; } = 10;
            public List<Elevator> Elevators { get; set; } = new();

            private readonly object _lock = new object();

            public Building()
            {
                for (int i = 1; i <= 3; i++)
                    Elevators.Add(new Elevator { Id = i });
            }

            public void AddCall(int floor, Direction direction)
            {
                lock (_lock)
                {
                    // Простейший алгоритм: выбираем ближайший свободный лифт или добавляем в цели
                    var bestElevator = Elevators.OrderBy(e => Math.Abs(e.CurrentFloor - floor)).FirstOrDefault();
                    if (bestElevator != null && !bestElevator.TargetFloors.Contains(floor))
                        bestElevator.TargetFloors.Add(floor);
                }
            }

            public void Update()
            {
                lock (_lock)
                {
                    foreach (var elevator in Elevators)
                    {
                        if (elevator.TargetFloors.Count == 0)
                        {
                            elevator.IsMoving = false;
                            elevator.CurrentDirection = Direction.Idle;
                            continue;
                        }

                        elevator.IsMoving = true;
                        int target = elevator.TargetFloors[0];
                        elevator.CurrentDirection = target > elevator.CurrentFloor ? Direction.Up : Direction.Down;

                        if (elevator.CurrentFloor == target)
                        {
                            elevator.TargetFloors.RemoveAt(0);
                            // Имитация высадки/посадки
                        }
                        else
                        {
                            elevator.CurrentFloor += elevator.CurrentDirection == Direction.Up ? 1 : -1;
                        }
                    }
                }
            }
        }

        public class MessageRequest { public string Message { get; set; } }
        public class SavedMessage { public string Id { get; set; } public string Text { get; set; } }
        public static class MessageStorage { public static List<SavedMessage> Messages = new(); }

        public class CallRequest { public int Floor { get; set; } public Direction Direction { get; set; } }

        public class FloorButtonViewModel
        {
            public int FloorNumber { get; set; }
            public RelayCommand<int> CallElevatorCommand { get; set; }
        }

        public class RelayCommand<T> : ICommand
        {
            private readonly Action<T> _execute;
            public event EventHandler CanExecuteChanged;
            public RelayCommand(Action<T> execute) => _execute = execute;
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) => _execute((T)parameter);
        }
        #endregion
    }
}
