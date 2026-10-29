using NAudio.CoreAudioApi;
using NAudio.Wave;
using var en = new MMDeviceEnumerator();
var mic = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
Console.WriteLine("default mic: " + mic.FriendlyName);
int buffers = 0; double maxRms = 0;
using var cap = new WasapiCapture(mic);
Console.WriteLine("mic format: " + cap.WaveFormat);
cap.DataAvailable += (s, e) =>
{
    buffers++;
    double sum = 0; int n = e.BytesRecorded / 4;
    for (int i = 0; i < n; i++) { float v = BitConverter.ToSingle(e.Buffer, i * 4); sum += (double)v * v; }
    double rms = n > 0 ? Math.Sqrt(sum / n) : 0;
    if (rms > maxRms) maxRms = rms;
};
cap.StartRecording();
System.Threading.Thread.Sleep(5000);
cap.StopRecording();
System.Threading.Thread.Sleep(500);
Console.WriteLine("mic buffers=" + buffers + " maxRms=" + maxRms.ToString("F6"));
