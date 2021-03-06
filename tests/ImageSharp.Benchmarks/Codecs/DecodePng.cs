// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System.IO;
using BenchmarkDotNet.Attributes;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Tests;
using SDImage = System.Drawing.Image;
using SDSize = System.Drawing.Size;

namespace SixLabors.ImageSharp.Benchmarks.Codecs
{
    [Config(typeof(Config.ShortClr))]
    public class DecodePng : BenchmarkBase
    {
        private byte[] pngBytes;

        private string TestImageFullPath => Path.Combine(TestEnvironment.InputImagesDirectoryFullPath, this.TestImage);

        [Params(TestImages.Png.Splash)]
        public string TestImage { get; set; }

        [GlobalSetup]
        public void ReadImages()
        {
            if (this.pngBytes == null)
            {
                this.pngBytes = File.ReadAllBytes(this.TestImageFullPath);
            }
        }

        [Benchmark(Baseline = true, Description = "System.Drawing Png")]
        public SDSize PngSystemDrawing()
        {
            using (var memoryStream = new MemoryStream(this.pngBytes))
            {
                using (var image = SDImage.FromStream(memoryStream))
                {
                    return image.Size;
                }
            }
        }

        [Benchmark(Description = "ImageSharp Png")]
        public Size PngCore()
        {
            using (var memoryStream = new MemoryStream(this.pngBytes))
            {
                using (var image = Image.Load<Rgba32>(memoryStream))
                {
                    return image.Size();
                }
            }
        }
    }
}
