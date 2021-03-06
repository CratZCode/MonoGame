// MonoGame - Copyright (C) The MonoGame Team
// This file is subject to the terms and conditions defined in
// file 'LICENSE.txt', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Xna;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

using SharpDX;
using SharpDX.XAudio2;
using SharpDX.Multimedia;
using SharpDX.X3DAudio;

namespace Microsoft.Xna.Framework.Audio
{
    public sealed partial class SoundEffect : IDisposable
    {
        #region Static Fields & Properties

        internal static XAudio2 Device { get; private set; }
        internal static MasteringVoice MasterVoice { get; private set; }

        private static X3DAudio _device3D;
        private static bool _device3DDirty = true;
        private static Speakers _speakers = Speakers.Stereo;

        // XNA does not expose this, but it exists in X3DAudio.
        [CLSCompliant(false)]
        public static Speakers Speakers
        {
            get { return _speakers; }

            set
            {
                if (_speakers == value)
                    return;
                
                _speakers = value;
                _device3DDirty = true;
            }
        }

        internal static X3DAudio Device3D
        {
            get
            {
                if (_device3DDirty)
                {
                    _device3DDirty = false;
                    _device3D = new X3DAudio(_speakers);
                }

                return _device3D;
            }
        }

        #endregion

        internal DataStream _dataStream;
        internal AudioBuffer _buffer;
        internal AudioBuffer _loopedBuffer;
        internal WaveFormat _format;

        // These three fields are used for keeping track of instances created
        // internally when Play is called directly on SoundEffect.
        private List<SoundEffectInstance> _playingInstances;
        private List<SoundEffectInstance> _availableInstances;
        private List<SoundEffectInstance> _toBeRecycledInstances;

        #region Initialization

        static SoundEffect()
        {
            InitializeSoundEffect();
        }

        internal static void InitializeSoundEffect()
        {
            try
            {
                if (Device == null)
                {
#if !WINRT && DEBUG
                    try
                    {
                        //Fails if the XAudio2 SDK is not installed
                        Device = new XAudio2(XAudio2Flags.DebugEngine, ProcessorSpecifier.DefaultProcessor);
                        Device.StartEngine();
                    }
                    catch
#endif
                    {
                        Device = new XAudio2(XAudio2Flags.None, ProcessorSpecifier.DefaultProcessor);
                        Device.StartEngine();
                    }
                }

                // Just use the default device.
#if WINRT
                string deviceId = null;
#else
                const int deviceId = 0;
#endif

                if (MasterVoice == null)
                {
                    // Let windows autodetect number of channels and sample rate.
                    MasterVoice = new MasteringVoice(Device, XAudio2.DefaultChannels, XAudio2.DefaultSampleRate, deviceId);
                    MasterVoice.SetVolume(_masterVolume, 0);
                }

                // The autodetected value of MasterVoice.ChannelMask corresponds to the speaker layout.
#if WINRT
                Speakers = (Speakers)MasterVoice.ChannelMask;
#else
                var deviceDetails = Device.GetDeviceDetails(deviceId);
                Speakers = deviceDetails.OutputFormat.ChannelMask;
#endif
            }
            catch
            {
                // Release the device and null it as
                // we have no audio support.
                if (Device != null)
                {
                    Device.Dispose();
                    Device = null;
                }

                MasterVoice = null;
            }
        }

        public void PlatformInitialize(byte[] buffer, int sampleRate, AudioChannels channels)
        {
            PlatformInitialize(buffer, 0, buffer.Length, sampleRate, channels, 0, buffer.Length);
        }

        private void PlatformInitialize(byte[] buffer, int offset, int count, int sampleRate, AudioChannels channels, int loopStart, int loopLength)
        {
            _format = new WaveFormat(sampleRate, (int)channels);

            _dataStream = DataStream.Create<byte>(buffer, true, false);

            // Use the loopStart and loopLength also as the range
            // when playing this SoundEffect a single time / unlooped.
            _buffer = new AudioBuffer()
            {
                Stream = _dataStream,
                AudioBytes = count,
                Flags = BufferFlags.EndOfStream,
                PlayBegin = loopStart,
                PlayLength = loopLength,
                Context = new IntPtr(42),
            };

            _loopedBuffer = new AudioBuffer()
            {
                Stream = _dataStream,
                AudioBytes = count,
                Flags = BufferFlags.EndOfStream,
                LoopBegin = loopStart,
                LoopLength = loopLength,
                LoopCount = AudioBuffer.LoopInfinite,
                Context = new IntPtr(42),
            };
        }

        private SoundEffectInstance PlatformCreateInstance()
        {
            SourceVoice voice = null;
            if (Device != null)
                voice = new SourceVoice(Device, _format, VoiceFlags.None, XAudio2.MaximumFrequencyRatio);

            return new SoundEffectInstance(this, voice);
        }

        private void PlatformLoadAudioStream(Stream s)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Play

        private bool PlatformPlay()
        {
            return PlatformPlay(1.0f, 0.0f, 0.0f);
        }

        private bool PlatformPlay(float volume, float pitch, float pan)
        {
            // TODO: While merging the SoundEffect classes together
            // I noticed that the return values seem to widly differ
            // between platforms. It also doesn't seem to match
            // what's written in the XNA docs.

            if (MasterVolume <= 0.0f)
                return true;
            
            if (_playingInstances == null)
            {
                // Allocate lists first time we need them.
                _playingInstances = new List<SoundEffectInstance>();
                _availableInstances = new List<SoundEffectInstance>();
                _toBeRecycledInstances = new List<SoundEffectInstance>();
            }
            else
            {
                // Cleanup instances which have finished playing.                    
                foreach (var inst in _playingInstances)
                {
                    if (inst.State == SoundState.Stopped)
                    {
                        _toBeRecycledInstances.Add(inst);
                    }
                }
            }

            // Locate a SoundEffectInstance either one already
            // allocated and not in use or allocate a new one.
            SoundEffectInstance instance = null;
            if (_toBeRecycledInstances.Count > 0)
            {
                foreach (var inst in _toBeRecycledInstances)
                {
                    _availableInstances.Add(inst);
                    _playingInstances.Remove(inst);
                }
                _toBeRecycledInstances.Clear();
            }
            if (_availableInstances.Count > 0)
            {
                instance = _availableInstances[0];
                _playingInstances.Add(instance);
                _availableInstances.Remove(instance);
            }
            else
            {
                instance = CreateInstance();
                _playingInstances.Add(instance);
            }

            instance.Volume = volume;
            instance.Pitch = pitch;
            instance.Pan = pan;
            instance.Play();

            // XNA documentation says this method returns false if the sound limit
            // has been reached. However, there is no limit on PC.
            return true;
        }

        #endregion

        private TimeSpan PlatformGetDuration()
        {
            var sampleCount = _buffer.PlayLength;
            var avgBPS = _format.AverageBytesPerSecond;

            return TimeSpan.FromSeconds((float)sampleCount / (float)avgBPS);
        }

        private static void PlatformSetMasterVolume()
        {
            MasterVoice.SetVolume(_masterVolume, 0);
        }

        private void PlatformDispose()
        {
            _dataStream.Dispose();
            isDisposed = true;
        }

        // Does someone actually need to call this if it only happens when the whole
        // game closes? And if so, who would make the call?
        internal static void Shutdown()
        {
            if (MasterVoice != null)
            {
                MasterVoice.DestroyVoice();
                MasterVoice.Dispose();
                MasterVoice = null;
            }

            if (Device != null)
            {
                Device.StopEngine();
                Device.Dispose();
                Device = null;
            }

            _device3DDirty = true;
            _speakers = Speakers.Stereo;
        }

    }
}

