using System;
using System.Collections.Generic;

namespace OpenFreqClient.Services.Interfaces;

public interface IAudioService: IDisposable
{
    public List<string> GetPlaybackDevices();
    public List<string> GetRecordingDevices();

    public void Init();
    public int DefaultPlaybackDevice { get; set; }
    public int DefaultRecordingDevice { get; set; }
}