using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Windows;

using NAudio.CoreAudioApi;
using NAudio.Wave;
using NHotkey.WindowsForms;
using ReactiveUI;

namespace OBSKeyboardMute
{
    public class MainWindowViewModel : ReactiveObject
    {
        public readonly ReactiveCommand<Unit, List<MMDevice>> UpdateDeviceList;

        ObservableAsPropertyHelper<List<MMDevice>> _DeviceList;
        public List<MMDevice> DeviceList => _DeviceList.Value;

        ObservableAsPropertyHelper<List<string>> _DeviceListNames;
        public List<string> DeviceListNames => _DeviceListNames.Value;

        int _SelectedDeviceIndex = -1;
        public int SelectedDeviceIndex {
            get => _SelectedDeviceIndex;
            set => this.RaiseAndSetIfChanged(ref _SelectedDeviceIndex, value);
        }

        ObservableAsPropertyHelper<MMDevice> _CurrentDevice;
        public MMDevice CurrentDevice => _CurrentDevice.Value;

        ObservableAsPropertyHelper<IObserver<float>> _VolumeSetter;
        public IObserver<float> VolumeSetter => _VolumeSetter.Value;

        public MainWindowViewModel()
        {
            UpdateDeviceList = ReactiveCommand.CreateFromTask(async _ => {
                var devEnum = new MMDeviceEnumerator();
                var devices = devEnum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                return devices.ToList();
            });

            UpdateDeviceList.ToProperty(this, x => x.DeviceList, out _DeviceList);
            UpdateDeviceList
                .Select(xs => xs.Select(x => x.FriendlyName).ToList())
                .ToProperty(this, x => x.DeviceListNames, out _DeviceListNames);

            this.WhenAnyValue(x => x.DeviceList, x => x.SelectedDeviceIndex).Select(x => {
                if (x.Item2 < 0 || x.Item2 >= x.Item1.Count) return null;
                return x.Item1[x.Item2];
            }).ToProperty(this, x => x.CurrentDevice, out _CurrentDevice);

            var beepPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), 
                "beep.wav");

            var emptyObserver = Observer.Create<float>(_ => { });

            this.WhenAnyValue(x => x.CurrentDevice)
                .Select(x => x != null ?
                    PlayTestTone(x, beepPath).Repeat() :
                    Observable.Return(emptyObserver).Concat(Observable.Never<IObserver<float>>()))
                .Switch()
                .ToProperty(this, x => x.VolumeSetter, out _VolumeSetter);

            var obsInput = new ObservableInput();

            var somethingHappened = Observable.Merge(
                obsInput.ListenToLowLevelKeyboard().Select(_ => Unit.Default),
                obsInput.ListenToLowLevelMouse()
                    .Where(x => x.MouseMessage != GlobalMouseHook.MouseMessages.WM_MOUSEMOVE) 
                    .Select(_ => Unit.Default)
            );

            Observable.Merge(
                somethingHappened.Select(_ => 1.0f),
                somethingHappened.GuaranteedThrottle(TimeSpan.FromMilliseconds(50), RxApp.MainThreadScheduler).Select(_ => 0.0f)
            ).Subscribe(x => VolumeSetter.OnNext(x));
        }

        public IObservable<IObserver<float>> PlayTestTone(MMDevice device, string pathToFile)
        {
            return Observable.Create<IObserver<float>>((subj) => {
                var cd = new CompositeDisposable();
                var af = new AudioFileReader(pathToFile);
                var waveOut = new WasapiOut(device, AudioClientShareMode.Shared, false, 20);

                cd.Add(waveOut);
                cd.Add(af);

                bool completed = false;
                waveOut.Init(af);
                waveOut.PlaybackStopped += (o, e) => {
                    completed = true;
                    subj.OnCompleted();
                };

                var obs = new Subject<float>();
                af.Volume = 0.0f;

                var lastVol = -1.0f;
                cd.Add(obs.Subscribe(x => {
                    if (lastVol == x) return;
                    lastVol = af.Volume = x;
                }));

                subj.OnNext(obs);

                waveOut.Play();

                cd.Add(Disposable.Create(() => {
                    if (completed) return;
                    waveOut.Stop();
                }));

                return cd;
            });
        }
    }

    public partial class MainWindow : Window, IViewFor<MainWindowViewModel>
    {
        public MainWindow()
        {
            InitializeComponent();
            ViewModel = new MainWindowViewModel();

            this.WhenActivated(d => {
                d(this.WhenAnyValue(x => x.ViewModel.DeviceListNames)
                    .Subscribe(x => DeviceListNames.ItemsSource = x));
                d(this.WhenAnyValue(x => x.DeviceListNames.SelectedIndex)
                    .BindTo(this, x => x.ViewModel.SelectedDeviceIndex));

            });

            var nullAsString = "Null!";
            this.WhenAnyValue(x => x.ViewModel.CurrentDevice)
                .Subscribe(x => Console.WriteLine($"Current Device is {(x != null ? x.FriendlyName : nullAsString)}"));

            ViewModel.UpdateDeviceList.Execute().Subscribe();
        }

        public MainWindowViewModel ViewModel {
            get => (MainWindowViewModel)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        public static readonly DependencyProperty ViewModelProperty =
            DependencyProperty.Register("ViewModel", typeof(MainWindowViewModel), typeof(MainWindow), new PropertyMetadata(null));
        
        object IViewFor.ViewModel { get => ViewModel; set => ViewModel = (MainWindowViewModel)value; }
    }
}
