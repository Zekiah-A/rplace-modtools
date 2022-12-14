using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using DynamicData;
using Microsoft.Extensions.DependencyInjection;
using rPlace.Models;
using SkiaSharp;
using rPlace.ViewModels;
using Websocket.Client;

namespace rPlace.Views;
public partial class MainWindow : Window
{
    private bool mouseDown;
    private Point mouseLast;
    private Point mouseTravel;
    private Point lookingAtPixel;
    private readonly HttpClient client = new();
    private readonly Cursor arrow = new (StandardCursorType.Arrow);
    private readonly Cursor cross = new(StandardCursorType.Cross);
    private WebsocketClient? socket;
    
    private MainWindowViewModel viewModel;
    //TODO: Switch these to using dependency injection
    private PaletteViewModel PVM => (PaletteViewModel) PaletteListBox.DataContext!;
    private ServerPresetViewModel SPVM => (ServerPresetViewModel) ServerPresetListBox.DataContext!;
    
    
    private Point LookingAtPixel
    {
        get
        {
            lookingAtPixel = new Point(
                Math.Clamp(Math.Floor(MainGrid.ColumnDefinitions[0].ActualWidth / 2 - Board.Left), 0, Board.CanvasWidth ?? 500),
                Math.Clamp(Math.Floor(Height / 2 - Board.Top), 0, Board.CanvasHeight ?? 500)
            );
            return lookingAtPixel;
        }
        set
        {
            lookingAtPixel = value;
            Board.Left = (float) Math.Floor(MainGrid.ColumnDefinitions[0].ActualWidth / 2 - lookingAtPixel.X);
            Board.Top = (float) Math.Floor(Height / 2 - lookingAtPixel.Y);
        }
    }

    private Point MouseOverPixel(PointerEventArgs e) => new(
        (int) Math.Clamp(Math.Floor(e.GetPosition(CanvasBackground).X - Board.Left), 0, Board.CanvasWidth ?? 500),
        (int) Math.Clamp(Math.Floor(e.GetPosition(CanvasBackground).Y - Board.Top), 0, Board.CanvasHeight ?? 500)
    );

    public MainWindow()
    {
        viewModel = App.Current.Services.GetRequiredService<MainWindowViewModel>();
        DataContext = viewModel;
        
        InitializeComponent();
        
        // Setting it from XAML causes the event to be fired before other controls get initialized which throws a NRE.
        PaintTool.IsChecked = true;
        
        CanvasBackground.AddHandler(PointerPressedEvent, OnBackgroundMouseDown, handledEventsToo: false);
        CanvasBackground.AddHandler(PointerMovedEvent, OnBackgroundMouseMove, handledEventsToo: false);
        CanvasBackground.AddHandler(PointerReleasedEvent, OnBackgroundMouseRelease, handledEventsToo: false);
        CanvasBackground.PointerWheelChanged += OnBackgroundWheelChanged;
        var windowSize = this.GetObservable(ClientSizeProperty);
        windowSize.Subscribe(size =>
        {
            if (size.Width > 500)
            {
                if (ToolsPanel.Classes.Contains("ToolsPanelClose")) return;
                ToolsPanel.Classes = Classes.Parse("ToolsPanelClose");
            }
            else
            {
                if (ToolsPanel.Classes.Contains("ToolsPanelOpen")) return;
                ToolsPanel.Classes = Classes.Parse("ToolsPanelOpen");
            }
        });
    }
    
    //Decompress changes so it can be put onto canv
    private byte[] RunLengthChanges(byte[] data)
    {
        var changeI = 0;
        Board.CanvasWidth = (int) BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan()[1..]);
        Board.CanvasHeight = (int) BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan()[5..]);
        var changes = new byte[(int) (Board.CanvasWidth * Board.CanvasHeight)];

        for (var i = 9; i < data.Length;)
        {
            var cell = data[i++];
            Console.WriteLine(cell);
            var c = cell >> 6;
            switch (c)
            {
                case 1:
                    c = data[i++];
                    break;
                case 2:
                    c = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan()[i++..]);
                    i++;
                    break;
                case 3:
                    c = (int) BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan()[i++..]);
                    i += 3;
                    break;
            }
            changeI += c;
            changes[changeI] = (byte) (cell & 63);
        }
        return changes;
    }

    private async Task CreateConnection(string uri)
    {
        var factory = new Func<ClientWebSocket>(() =>
        { 
            var wsClient = new ClientWebSocket
            {
                Options = { KeepAliveInterval = TimeSpan.FromSeconds(5) }
            };
            wsClient.Options.SetRequestHeader("Origin", "https://rplace.tk");
            return wsClient;
        });
        socket = new WebsocketClient(new Uri(uri), factory);
        socket.ReconnectTimeout = TimeSpan.FromSeconds(10);
        
        socket.ReconnectionHappened.Subscribe(info => Console.WriteLine("Reconnected to {0}, {1}", uri, info.Type));
        socket.MessageReceived.Subscribe(msg =>
        {
            var code = msg.Binary[0];
            switch (code)
            {
                case 2:
                    Board.Changes = RunLengthChanges(msg.Binary);
                    break;
                case 6: //Incoming pixel someone else sent
                {
                    var i = 0;
                    while (i < msg.Binary.Length - 2)
                    {
                        var pos = BinaryPrimitives.ReadUInt32BigEndian(msg.Binary.AsSpan()[(i += 1)..]);
                        var col = msg.Binary[i += 4];
                        Board.Set(new Pixel
                        {
                            Colour = col,
                            Width = Board.CanvasWidth ?? 500,
                            Height = Board.CanvasHeight ?? 500,
                            Index = (int) pos
                        });
                    }
                    break;
                }
                case 7: //Sending back what you placed
                {
                    var pos = BitConverter.ToUInt32(msg.Binary.ToArray(), 5);
                    var col = msg.Binary[9];
                    break;
                }
            }
        });
        socket.DisconnectionHappened.Subscribe(info => Console.WriteLine("Disconnected from {0}, {1}", uri, info.Exception));
        
        await socket.Start();
    }

    private void RollbackArea(int x, int y, int w, int h, byte[] rollbackBoard)
    {
        if (w > 250 || h > 250 || x >= Board.CanvasWidth || y >= Board.CanvasHeight) return;
        var buffer = new byte[Board.CanvasWidth ?? 500 * Board.CanvasHeight ?? 500 + 7];
        var i = x + y * Board.CanvasWidth ?? 500;
        new byte[] {99, (byte) w, (byte) h, (byte) (i >> 24), (byte) (i >> 16), (byte) (i >> 8), (byte) i}.CopyTo(buffer, 0);
        
        for (var hi = 0; hi < h; hi++)
        {
            BinaryPrimitives.WriteInt32BigEndian(rollbackBoard.AsSpan().Slice(i, i + w),hi * w + 7);
            rollbackBoard[i..(i + w)].CopyTo(buffer, hi * w + 7);
            i += Board.CanvasWidth ?? 500;
        }
        
        if (socket is {IsRunning: true}) socket.Send(buffer);
    }

    private async Task FetchCacheBackuplist()
    {
        var responseBody = await (await Fetch(viewModel?.CurrentPreset.FileServer + "backuplist")).Content.ReadAsStringAsync();
        var stack = new Stack(responseBody.Split("\n"));
        stack.Pop();
        stack.Push("place");
        var backupArr = (object[]) stack.ToArray();
        CanvasDropdown.Items = backupArr;
    }
    
    private async Task<HttpResponseMessage> Fetch(string? uri)
    {
        try
        {
            var response = await client.GetAsync(uri);
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch (HttpRequestException e) { Console.WriteLine(e); }
        return new HttpResponseMessage();
    }

    private async Task<Bitmap?> CreateCanvasPreviewImage<T>(T input)
    {
        var placeFile = input switch
        {
            string uri => await (await Fetch(uri)).Content.ReadAsByteArrayAsync(),
            byte[] board => board,
            _ => null
        };
        if (placeFile is null) return null;
        
        using var bmp = new SKBitmap(Board.CanvasWidth ?? 500, Board.CanvasHeight ?? 500, true);
        for (var i = 0; i < placeFile.Length; i++)
            bmp.SetPixel(i % Board.CanvasWidth ?? 500, i / Board.CanvasWidth ?? 500, PaletteViewModel.Colours.ElementAtOrDefault(placeFile[i])); //ElementAtOrDefault is safer than direct index
        using var bitmap = bmp.Encode(SKEncodedImageFormat.Png, 100);
        await using var imgStream = new MemoryStream();
        imgStream.Position = 0;
        bitmap.SaveTo(imgStream);
        imgStream.Seek(0, SeekOrigin.Begin);
        bmp.Dispose();
        bitmap.Dispose();
        return new Bitmap(imgStream);
    }

    //App started
    private async void OnStartButtonPressed(object? sender, RoutedEventArgs e)
    {
        //Configure the current session's data
        if (!ServerPresetViewModel.ServerPresetExists(viewModel!.CurrentPreset)) ServerPresetViewModel.SaveServerPreset(viewModel.CurrentPreset);
        
        //UI and connections
        await CreateConnection(viewModel.CurrentPreset.Websocket + viewModel.CurrentPreset.AdminKey);
        Board.Board = await (await Fetch(viewModel.CurrentPreset.FileServer + "place")).Content.ReadAsByteArrayAsync();
        PlaceConfigPanel.IsVisible = false;
        DownloadBtn.IsEnabled = true;

        //Backup loading
        await FetchCacheBackuplist();
        CanvasDropdown.SelectedIndex = 0; //BackupCheckInterval()
    }

    private void OnServerPresetsSelectionChanged(object? sender, SelectionChangedEventArgs args) =>
        viewModel!.CurrentPreset = SPVM.ServerPresets[ServerPresetListBox.SelectedIndex];

    private void OnBackgroundMouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is null || !e.Source.Equals(CanvasBackground)) return; //stop bubbling
        mouseTravel = new Point(0, 0);
        mouseDown = true;
        if (viewModel!.CurrentTool == Tool.Select) Board.StartSelection(MouseOverPixel(e), MouseOverPixel(e));
    }

    private void OnBackgroundMouseMove(object? sender, PointerEventArgs e)
    {
        if (e.Source is null || !e.Source.Equals(CanvasBackground)) return;
        if (mouseDown)
        {
            //If left mouse button, go to colour picker mode from the canvas instead
            if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
            {
                if (e.GetPosition(CanvasBackground).X - Board.Left < 0 || e.GetPosition(CanvasBackground).X - Board.Left > (Board.CanvasWidth ?? 500) * Board.Zoom ||
                    e.GetPosition(CanvasBackground).Y - Board.Left < 0 || e.GetPosition(CanvasBackground).Y - Board.Left > (Board.CanvasHeight ?? 500) * Board.Zoom) return;
                CursorIndicatorRectangle.IsVisible = true;
                Canvas.SetLeft(CursorIndicatorRectangle, e.GetPosition(CanvasBackground).X + 8);
                Canvas.SetTop(CursorIndicatorRectangle, e.GetPosition(CanvasBackground).Y + 8);
                Cursor = cross;
                //If mouse if over board, then get pixel colour at that position.
                var pxCol = Board.ColourAt((int)Math.Floor(e.GetPosition(CanvasBackground).X), (int)Math.Floor(e.GetPosition(CanvasBackground).Y));
                if (pxCol is null) return;
                if (PaletteViewModel.Colours.IndexOf((SKColor) pxCol) > 0) PVM.CurrentColour = PaletteViewModel.Colours.IndexOf((SKColor) pxCol);
                CursorIndicatorRectangle.Background = new SolidColorBrush(new Color(pxCol.Value.Alpha, pxCol.Value.Red, pxCol.Value.Green, pxCol.Value.Blue));
                return;
            }
            if (viewModel?.CurrentTool == Tool.Select)
            {
                Board.UpdateSelection(null, MouseOverPixel(e));
                return;
            }
            if (PVM.CurrentColour is not null) //drag place pixels
            {
                var px = new Pixel {
                    Colour = PVM.CurrentColour ?? 0,
                    X = (int) Math.Floor(MouseOverPixel(e).X),
                    Y = (int) Math.Floor(MouseOverPixel(e).Y),
                    Width = Board.CanvasWidth ?? 500,
                    Height = Board.CanvasHeight ?? 500
                };
                SetPixels(px, viewModel!.CurrentPaintBrushRadius);
                //SetPixels(px, 20);
                return;
            }
            
            //Multiply be 1/zoom so that it always moves at a speed to make it seem to drag with the mouse regardless of zoom level
            Board.Left += (float) (e.GetPosition(CanvasBackground).X - mouseLast.X) * (1 / Board.Zoom);
            Board.Top += (float) (e.GetPosition(CanvasBackground).Y - mouseLast.Y) * (1 / Board.Zoom);
            //Clamp values
            Board.Left = (float) Math.Clamp(Board.Left, MainGrid.ColumnDefinitions[0].ActualWidth / 2 - (Board.CanvasWidth ?? 500) * Board.Zoom, MainGrid.ColumnDefinitions[0].ActualWidth / 2);
            Board.Top = (float) Math.Clamp(Board.Top, Height / 2 - (Board.CanvasHeight ?? 500) * Board.Zoom, Height / 2);
        }
        else
        {
            Cursor = arrow;
            CursorIndicatorRectangle.IsVisible = false;
        }
        mouseTravel += new Point(Math.Abs(e.GetPosition(CanvasBackground).X - mouseLast.X), Math.Abs(e.GetPosition(CanvasBackground).Y - mouseLast.Y));
        mouseLast = e.GetPosition(CanvasBackground);
    }
    
    private void OnBackgroundMouseRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is null || !e.Source.Equals(CanvasBackground)) return;
        mouseDown = false;
        
        //click place pixel
        if (PVM.CurrentColour is null) return;
        var px = new Pixel
        {
            Colour = PVM.CurrentColour ?? 0,
            X = (int) Math.Floor(MouseOverPixel(e).X),
            Y = (int) Math.Floor(MouseOverPixel(e).Y),
            Width = Board.CanvasWidth ?? 500,
            Height = Board.CanvasHeight ?? 500
        };
        //SetPixels(px, 50);
        SetPixels(px, viewModel.CurrentPaintBrushRadius);
        
        PVM.CurrentColour = null;
    }

    //https://github.com/rslashplace2/rslashplace2.github.io/blob/1cc30a12f35a6b0938e538100d3337228087d40d/index.html#L531
    private void OnBackgroundWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        //Board.Left -= (float) (e.GetPosition(this).X - MainGrid.ColumnDefinitions[0].ActualWidth / 2) / 50; //Board.Top -= (float) (e.GetPosition(this).Y - Height / 2) / 50;
        LookingAtPixel = new Point((float) (e.GetPosition(Board).X), (float) (e.GetPosition(Board).Y));
        Board.Zoom += (float) e.Delta.Y / 10;
    }

    private async void OnCanvasDropdownSelectionChanged(object? _, SelectionChangedEventArgs e)
    {
        if (CanvasDropdown is null) return;
        var backupName = CanvasDropdown.SelectedItem as string ?? "place";
        PreviewImg.Source = await CreateCanvasPreviewImage(viewModel.CurrentPreset.FileServer + "backups/" + backupName);
        
        if (ViewSelectedDate.IsChecked is true)
        {
            //TODO: This is essentially same as OnViewSelectedDateClicked
            Board.Board = await (await Fetch(viewModel.CurrentPreset.FileServer + "place")).Content.ReadAsByteArrayAsync();
            Board.SelectionBoard = await (await Fetch(viewModel.CurrentPreset.FileServer + "backups/" + backupName)).Content.ReadAsByteArrayAsync();
            viewModel.StateInfo = null;
        }
        else
        {
            Board.Board = await (await Fetch(viewModel.CurrentPreset.FileServer + "backups/" + backupName)).Content.ReadAsByteArrayAsync();
            Board.Changes = null;
            Board.ClearSelections();
            viewModel.StateInfo = CanvasDropdown.SelectedIndex != 0 ? App.Current.Services.GetRequiredService<LiveCanvasStateInfoViewModel>() : null;
        }
    }
    
    private async void OnViewSelectedDateChecked(object? sender, RoutedEventArgs e)
    {
            Board.Board = await (await Fetch(viewModel.CurrentPreset.FileServer + "place")).Content.ReadAsByteArrayAsync();
            var backupName = CanvasDropdown.SelectedItem as string ?? "place";
            Board.SelectionBoard = await (await Fetch(viewModel.CurrentPreset.FileServer + "backups/" + backupName)).Content.ReadAsByteArrayAsync();
            viewModel.StateInfo = null;
    }

    private async void OnViewSelectedDateUnchecked(object? sender, RoutedEventArgs e)
    {
        Board.Board = await (await Fetch(viewModel.CurrentPreset.FileServer + "backups/" + CanvasDropdown.SelectedItem)).Content.ReadAsByteArrayAsync();
        Board.Changes = null;
        Board.ClearSelections();
        viewModel.StateInfo = CanvasDropdown.SelectedIndex != 0 ? App.Current.Services.GetRequiredService<LiveCanvasStateInfoViewModel>() : null;
    }
    
    private void OnResetCanvasViewPressed(object? _, RoutedEventArgs e)
    {
        Board.Left = 0;
        Board.Top = 0;
        Board.Zoom = 1;
    }
    
    private void OnSelectColourClicked(object? sender, RoutedEventArgs e) => Palette.IsVisible = true;
    private void OnPaletteDoneButtonClicked(object? sender, RoutedEventArgs e) => Palette.IsVisible = false;
    private void OnPaletteSelectionChanged(object? sender, SelectionChangedEventArgs e) =>  PVM.CurrentColour = (sender as ListBox)?.SelectedIndex ?? PVM.CurrentColour;
    private async void OnDownloadPreviewPressed(object? sender, RoutedEventArgs e)
    {
        var path = await ShowSaveFileDialog(
            (CanvasDropdown.SelectedItem as string ?? "place") + "_preview.png",
            "Download place file image to system"
        );
        if (path is null) return;
        var placeImg = await CreateCanvasPreviewImage(viewModel.CurrentPreset.FileServer + "backups/" + CanvasDropdown.SelectedItem);
        placeImg?.Save(path);
    } 

    private async Task<string?> ShowSaveFileDialog(string fileName, string title)
    {
        var dialog = new SaveFileDialog
        {
            InitialFileName = fileName,
            Title = title
        };
        return await dialog.ShowAsync(this);
    }
    
    private void SetPixels(Pixel px, int radius)
    {
        if (radius == 1)
        {
            Board.Set(px);
            if (socket is {IsRunning: true}) socket.Send(px.ToByteArray());
            return;
        }

        var radiusStack = new Stack<Pixel>();
        if (viewModel.CurrentBrushShape == Shape.Square)
        {
            var diameter = 2 * radius + 1;
            for (var i = 0; i < diameter; i++)
            {
                for (var j = 0; j < diameter; j++)
                {
                    var y = j-radius;
                    var x = i-radius;
                    
                    if (x * x + y * y > radius * radius + 1) continue;
                    var radiusPx = px.Clone();
                    radiusPx.X += x;
                    radiusPx.Y += y;
                    Board.Set(radiusPx);
                    radiusStack.Push(radiusPx);
                }
            }
        }
        else
        {
            for (var x = 0 - radius / 2; x < radius / 2; x++)
            {
                for (var y = 0 - radius / 2; y < radius / 2; y++)
                {
                    var radiusPx = px.Clone();
                    radiusPx.X += x;
                    radiusPx.Y += y;
                    Board.Set(radiusPx);
                    radiusStack.Push(radiusPx);
                }
            }
        }
        
        Task.Run(() =>
        {
            while (radiusStack.Count != 0)
            {
                Thread.Sleep(30);
                if (socket is {IsRunning: true}) socket.Send(radiusStack.Pop().ToByteArray());
            }
        });
    }

    private void OnToggleThemePressed(object? sender, RoutedEventArgs e)
    {
        var currentStyle = (FluentTheme) Application.Current?.Styles[0]!;
        currentStyle.Mode = currentStyle.Mode == FluentThemeMode.Dark ? FluentThemeMode.Light : FluentThemeMode.Dark;
    }

    private void OnOpenGithubClicked(object? sender, RoutedEventArgs e)
    {
        string? processName = null;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD())
            processName = "xdg-open";
        else if (OperatingSystem.IsWindows())
            processName = "";
        else if (OperatingSystem.IsMacOS())
            processName = "open";
        if (processName is null) return;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = processName,
                Arguments = "https://github.com/Zekiah-A/rplace-modtools"
            }
        };
        process.Start();
    }

    private void OnRollbackButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (Board.SelectionBoard is null) return;
        var sel = Board.Selections.Peek();
        RollbackArea((int) sel.Tl.X, (int) sel.Tl.Y, (int) sel.Br.X - (int) sel.Tl.X, (int) sel.Br.Y - (int) sel.Tl.Y, Board.SelectionBoard);
    }

    private async Task BackupCheckInterval()
    {

        //Wait 15 mins before checking again, TODO: Make this configurable with toggle backup check interval
        while (true)
        {
            Thread.Sleep(900000);
            await FetchCacheBackuplist();
            //If we are already viewing place update it
            if (CanvasDropdown.SelectedIndex == 0)
            {
                Board.Board = await (await Fetch(viewModel.CurrentPreset.FileServer + "place")).Content.ReadAsByteArrayAsync();
            }
        }
    }

    private void ToolToggleButtonCheck(object? sender, RoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(sender);
        
        var toggleButton = (ToggleButton) sender;
        
        switch (toggleButton.Name)
        {
            case "PaintTool":
            {
                RubberTool.IsChecked = false;
                SelectTool.IsChecked = false;
                break;
            }
                
            case "RubberTool":
            {
                PaintTool.IsChecked = false;
                SelectTool.IsChecked = false;
                break;
            }

            case "SelectTool":
            {
                RubberTool.IsChecked = false;
                PaintTool.IsChecked = false;
                break;
            }
        }
    }
}
