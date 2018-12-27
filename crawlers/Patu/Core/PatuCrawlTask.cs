using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using log4net;
using Patu.HttpClientFactories;
using Patu.Processor;

namespace Patu
{
    public class PatuCrawlTask : IDisposable, ICrawlContext
    {
        private static ILog Logger = Utility.GetLogger(typeof(PatuCrawlTask));

        private bool disposable = false;
        private CancellationTokenSource cancellation;
        private BloomFilter<string> bloom;
        private ConcurrentQueue<string> seeds{get;set;}
        private IProcessor processor;
        /// <summary>
        /// Limit the number of tasks to twice the number of system processors
        /// </summary>
        private Semaphore semaphore = new Semaphore(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
        private int activeTask = 0;
        private PatuConfig config;
        private int crawlingDeepth = 1;
        private ConcurrentBag<string> seedBag{get;set;} = new ConcurrentBag<string>();
        private string id = Guid.NewGuid().ToString();
        private IHttpClientFactory httpClientFactory;

        public string Id{get => id;}
        public bool Finished{get; private set;} = false;

        public PatuCrawlTask(PatuConfig config, IEnumerable<string> seeds,
            IProcessor processor, CancellationTokenSource cancellation = null,
            IHttpClientFactory httpClientFactory = null)
        {
            if(cancellation != null)
            {
                this.cancellation = cancellation;
            }
            else
            {
                this.cancellation = new CancellationTokenSource();
                this.disposable = true;
            }
            this.bloom = new BloomFilter<string>(config.BloomSize, config.ExpectedPageCount);
            this.seeds = new ConcurrentQueue<string>(seeds);
            this.processor = processor;
            this.config = config;
            this.httpClientFactory = httpClientFactory;
        }

        public Task Start()
        {
            return Task.Run(() =>
            {
                Logger.Info($"CrawlingDeepth: {this.crawlingDeepth}");
                while(true)
                {
                    if(this.seeds.Count > 0)
                    {
                        this.GetSeedAndProcess();
                    }
                    else
                    {
                        SpinWait.SpinUntil(() => this.activeTask == 0);
                        Interlocked.Increment(ref this.crawlingDeepth);
                        if(this.crawlingDeepth > this.config.CrawlDeepth)
                        {
                            this.Finished = true;
                            Logger.Info($"PatuCrawlTask({this.Id}) has crawled {this.bloom.Count} pages and finishing his work now!");
                            return;
                        }

                        Logger.Info($"CrawlingDeepth: {this.crawlingDeepth}");
                        while(this.seedBag.Count > 0)
                        {
                            string seed = null;
                            if(this.seedBag.TryTake(out seed))
                            {
                                this.seeds.Enqueue(seed);
                            }
                        }
                    }
                }
            }, this.cancellation.Token);
        }

        public double GetMisjudgmentRate()
        {
            return this.bloom.FalsePositiveProbability();
        }

        public void Dispose()
        {
            if(this.disposable)
            {
                this.cancellation.Cancel();
                this.cancellation.Dispose();
            }
        }

        public void AddSeeds(params string[] seeds)
        {
            if(seeds != null)
            {
                foreach(var seed in seeds)
                {
                    if(this.config.TargetHosts == null || this.config.TargetHosts.Count == 0
                        || seed.ContainsAnySubstring(this.config.TargetHosts))
                    {
                        this.seedBag.Add(seed);
                    }
                }
            }
        }

        private void GetSeedAndProcess()
        {
            this.semaphore.WaitOne();
            Interlocked.Increment(ref this.activeTask);
            Logger.Info($"active task: {this.activeTask}");
            string seed = null;
            if(!this.seeds.TryDequeue(out seed))
            {
                Interlocked.Decrement(ref this.activeTask);
                this.semaphore.Release();
                return;
            }
            if(!Utility.IsUrl(seed))
            {
                Logger.Warn($"{seed} is not a valid url.");
                Interlocked.Decrement(ref this.activeTask);
                this.semaphore.Release();
                return;
            }
            if(this.bloom.Contains(seed))
            {
                Interlocked.Decrement(ref this.activeTask);
                this.semaphore.Release();
                return;
            }

            lock(this.bloom)
            {
                if(this.bloom.Contains(seed))
                {
                    Logger.Debug($"The BloomFilter refused {seed}");
                    Interlocked.Decrement(ref this.activeTask);
                    this.semaphore.Release();
                    return;
                }
                else
                {
                    this.bloom.Add(seed);
                    Logger.Info($"PatuCrawlTask({this.Id}) has crawled {this.bloom.Count} pages and tasks still running.");
                }
            }

            Task.Run(() =>
            {
                try
                {
                    var page = new HtmlPage
                    {
                        Url = seed,
                        Uri = new Uri(seed),
                        Content = Utility.FetchHtml(seed, this.httpClientFactory?.GetHttpClient()),
                        Document = new HtmlDocument()
                    };
                    page.Document.LoadHtml(page.Content);
                    this.processor.Process(page, this);
                }
                catch(Exception e)
                {
                    Logger.Error($"Catched an exception when processing {seed}", e);
                }

                Interlocked.Decrement(ref this.activeTask);
                this.semaphore.Release();
            }, this.cancellation.Token);
        }
    }
}