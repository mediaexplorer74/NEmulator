﻿using System;
using System.Threading;
using OpenTK;
using OpenTK.Audio;
using OpenTK.Audio.OpenAL;

namespace AudioTest
{
    class AudioTest
    {
        AudioContext context;

        int sourceNum;
        private uint trisource;
        private IntPtr sinData1;
        private IntPtr sinData2;
        private int sampleFreq = 22050;
        private const int NUM_AUDIO_BUFFERS = 30;

        public static void Main(string[] args)
        {
            AudioTest test = new AudioTest();
            test.Run();
        }

        public void Run()
        {
            using (context = new AudioContext())
            {
                Console.WriteLine("Version: {0}", AL.Get(ALGetString.Version));
                Console.WriteLine("Vendor: {0}", AL.Get(ALGetString.Vendor));
                Console.WriteLine("Renderer: {0}", AL.Get(ALGetString.Renderer));

                int[] tribuffers, sinbuffers, sources;
                
                sinbuffers = AL.GenBuffers(2);
                tribuffers = AL.GenBuffers(NUM_AUDIO_BUFFERS);
                sources = AL.GenSources(1);

                int sampleRate = 44100;
                //var sinData1 = generateSinWave(100, sampleFreq, 1000);
                //var sinData2 = generateSinWave(1500, sampleFreq, 500);
                //var triData1 = generateTriWave(440, sampleFreq, 500);
                //var triData2 = generateTriWave(100, sampleFreq, 500);
                short[][] sqData = new short[NUM_AUDIO_BUFFERS][];

                //AL.BufferData(tribuffers[1], ALFormat.Mono16, triData2, triData2.Length, sampleFreq);

                //AL.Source(trisource, ALSourcei.Buffer, tribuffer);
                //AL.Source(trisource, ALSourceb.Looping, true);
                //AL.SourcePlay(trisource);

                //Console.WriteLine("Triangle Waves");

                int oscillations = 0;
                Random rnd = new Random();
                for (;oscillations < 75; oscillations++)
                {
                    sqData[oscillations % NUM_AUDIO_BUFFERS] 
                        = generateSquareWave(rnd.Next(100, 1000), sampleRate, rnd.Next(100, 200));
                    //sqData[1] = generateSquareWave(500, sampleRate, 100);
                    AL.BufferData(tribuffers[oscillations % NUM_AUDIO_BUFFERS], ALFormat.Mono16, sqData[oscillations % NUM_AUDIO_BUFFERS], sqData[oscillations % NUM_AUDIO_BUFFERS].Length, sampleRate);
                    AL.SourceQueueBuffer(sources[0], tribuffers[oscillations % NUM_AUDIO_BUFFERS]);
                    AL.SourceUnqueueBuffer(sources[0]);
                    if (AL.GetSourceState(sources[0]) != ALSourceState.Playing)
                        AL.SourcePlay(sources[0]);
                }
                do
                {
                    oscillations++;
                    sourceNum = oscillations % 2;
                    AL.SourceQueueBuffer(sources[0], tribuffers[sourceNum]);
                } while (oscillations <= 5);

                AL.SourceStop(trisource);

                Console.WriteLine("Press a key to play sine wave");
                Console.ReadKey();
                AL.SourceUnqueueBuffer(sources[0]);

                AL.BufferData((uint)sinbuffers[0], ALFormat.Mono16, sinData1, 
                    /*sinData1.Length*/10000, sampleFreq);
                //AL.Source(sinsources[0], ALSourcei.Buffer, sinbuffers[0]);
                AL.BufferData(sinbuffers[1], ALFormat.Mono16, sinData2,
                    /*sinData2.Length*/10000, sampleFreq);
                //AL.Source(sinsources[1], ALSourcei.Buffer, sinbuffers[1]);
                //AL.Source(sinsources[1], ALSourceb.Looping, true);


                oscillations = 0;
                sourceNum = oscillations % 2;
                AL.SourceQueueBuffer(sources[0], sinbuffers[sourceNum]);
                AL.SourcePlay(sources[sourceNum]);

                do
                {
                    oscillations++;
                    sourceNum = oscillations % 2;
                    AL.SourceQueueBuffer(sources[0], sinbuffers[sourceNum]);
                } while (oscillations <= 5);

                Console.WriteLine("Sine Wave - Press a key to stop");
                Console.ReadKey();
            }
        }

        private short[] generateSinWave(int freq, int sampleRate, int sampleLengthMS)
        {
            double dt = 2 * Math.PI / sampleRate;
            double amp = 0.5 * short.MaxValue;
            var dataCount = sampleRate * (sampleLengthMS / 1000.0f);
            var sinData = new short[(int)dataCount];

            for (int i = 0; i < sinData.Length; i++)
            {
                sinData[i] = (short)(amp * Math.Sin(i * dt * freq));
            }

            return sinData;
        }

        private short[] generateTriWave(int freq, int sampleRate, int sampleLengthMS)
        {
            short period = (short)((sampleRate / freq) - 1);
            var dataCount = (int)(sampleRate * (sampleLengthMS / 1000.0f));
            var triData = new short[dataCount];
            double amp = short.MaxValue * 0.5;

            for (int i = 0; i < triData.Length; i++)
            {
                triData[i] = (short)((1 - 2 * Math.Abs(Math.Round((double)((1/period) * i), 
                    MidpointRounding.AwayFromZero) - ((1.0d/period) * i))) * amp);
            }

            return triData;
        }

        private short[] generateSquareWave(int freq, int sampleRate, int sampleLengthMS)
        {
            short period = (short)((sampleRate / freq) - 1);
            int dataCount = (int)(sampleRate * (sampleLengthMS / 1000.0f));
            double dt = 2 * Math.PI / sampleRate;
            var data = new short[dataCount];
            double amp = short.MaxValue * 0.25;

            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (short)(amp * Math.Sign(Math.Sin(i * dt * freq)));
            }

            return data;
        }
    }
}
