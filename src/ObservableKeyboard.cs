using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NHotkey;
using NHotkey.WindowsForms;
using ReactiveUI;
using RegistryUtils;
using Microsoft.Win32;
using System.Reactive.Concurrency;
using Splat;

namespace OBSKeyboardMute
{
    public interface IObservableInput : IDisposable
    {
        IObservable<Unit> InputProfileChanged { get; }
        IObservable<Unit> ListenHotkey(Keys key);

        IObservable<GlobalKeyboardHookEventArgs> ListenToLowLevelKeyboard();
        IObservable<GlobalMouseHookEventArgs> ListenToLowLevelMouse();
    }

    public class ObservableInput : IObservableInput, IEnableLogger
    {
        readonly HotkeyManager hotkeyManager;

        public ObservableInput(HotkeyManager hkm = null)
        {
            hotkeyManager = hkm ?? HotkeyManager.Current;
            var inputProfileChanged = Observable.Create<Unit>((subj) => {
                var regmon = new RegistryMonitor(RegistryHive.CurrentUser, "Software\\Microsoft\\Input\\Locales");

                regmon.RegChangeNotifyFilter = RegChangeNotifyFilter.Value;
                regmon.RegChanged += (o, e) => RxApp.MainThreadScheduler.Schedule(() => subj.OnNext(Unit.Default));
                regmon.Error += (o, e) => RxApp.MainThreadScheduler.Schedule(() => subj.OnError(e.GetException()));

                regmon.Start();

                return Disposable.Create(() => {
                    regmon.Stop();
                    regmon.Dispose();
                });
            });

            InputProfileChanged = inputProfileChanged
                .LoggedCatch(this, Observable.Never<Unit>(), "Failed to observe locale changes, user maybe has only one locale?")
                .Publish().RefCount();
        }

        public IObservable<Unit> InputProfileChanged { get; private set; }

        public IObservable<Unit> ListenHotkey(Keys key)
        {
            var ret = Observable.Create<Unit>((subj) => {
                var name = Guid.NewGuid().ToString();
                var eh = new EventHandler<HotkeyEventArgs>((o, e) => subj.OnNext(Unit.Default));

                try {
                    hotkeyManager.AddOrReplace(name, key, true, eh);
                } catch (Exception ex) {
                    subj.OnError(ex);
                    return Disposable.Empty;
                }

                return Disposable.Create(() => hotkeyManager.Remove(name));
            });

            return ret
                .SubscribeOn(RxApp.MainThreadScheduler)
                .Publish().RefCount();
        }

        // NB: We really only can have exactly *one* global keyboard hook, so we will
        // make sure that anyone trying to listen to it will get that one
        static Lazy<IObservable<GlobalKeyboardHookEventArgs>> ghkKeyboardObservable = new Lazy<IObservable<GlobalKeyboardHookEventArgs>>(() => {
            var ret = Observable.Create<GlobalKeyboardHookEventArgs>((subj) => {
                var ghk = new GlobalKeyboardHook();
                ghk.KeyboardPressed += (o, e) => subj.OnNext(e);

                return ghk;
            });

            // NB: We are firing this event for literally every keypress - we
            // need to spend as little time as possible in the hook procedure
            // or we risk Windows unhooking us
            return ret
                .SubscribeOn(RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.TaskpoolScheduler)
                .Publish().RefCount();
        }, false);

        public IObservable<GlobalKeyboardHookEventArgs> ListenToLowLevelKeyboard()
        {
            return ghkKeyboardObservable.Value;
        }

        // NB: We really only can have exactly *one* global keyboard hook, so we will
        // make sure that anyone trying to listen to it will get that one
        static Lazy<IObservable<GlobalMouseHookEventArgs>> ghkMouseObservable = new Lazy<IObservable<GlobalMouseHookEventArgs>>(() => {
            var ret = Observable.Create<GlobalMouseHookEventArgs>((subj) => {
                var ghk = new GlobalMouseHook();
                ghk.MouseInput += (o, e) => subj.OnNext(e);

                return ghk;
            });

            // NB: We are firing this event for literally every keypress - we
            // need to spend as little time as possible in the hook procedure
            // or we risk Windows unhooking us
            return ret
                .SubscribeOn(RxApp.MainThreadScheduler)
                .ObserveOn(RxApp.TaskpoolScheduler)
                .Publish().RefCount();
        }, false);

        public IObservable<GlobalMouseHookEventArgs> ListenToLowLevelMouse()
        {
            return ghkMouseObservable.Value;
        }
        public void Dispose() => hotkeyManager.Dispose();
    }
}
