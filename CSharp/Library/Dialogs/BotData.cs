﻿// 
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.
// 
// Microsoft Bot Framework: http://botframework.com
// 
// Bot Builder SDK Github:
// https://github.com/Microsoft/BotBuilder
// 
// Copyright (c) Microsoft Corporation
// All rights reserved.
// 
// MIT License:
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using Microsoft.Bot.Builder.Internals.Fibers;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Microsoft.Bot.Builder.Dialogs.Internals
{
    public enum BotStoreType
    {
        BotConversationData,
        BotPrivateConversationData,
        BotUserData
    }

    public class BotDataKey
    {
        public string UserId { get; set; }
        public string ConversationId { get; set; }
        public string BotId { get; set; }
        public string ChannelId { get; set;}

        public override bool Equals(object obj)
        {
            if (obj == null || !(obj is BotDataKey))
            {
                return false;
            }
            else
            {
                var otherKey = (BotDataKey)obj;
                return otherKey.UserId == this.UserId &&
                    otherKey.ConversationId == this.ConversationId &&
                    otherKey.ChannelId == this.ChannelId &&
                    otherKey.BotId == this.BotId;
            }
        }

        public override int GetHashCode()
        {
            return UserId.GetHashCode() +
                ConversationId.GetHashCode() +
                BotId.GetHashCode() +
                ChannelId.GetHashCode(); 
        }
    }
    
    public interface IBotDataStore<T>
    {
        Task<T> LoadAsync(BotDataKey key, BotStoreType botStoreType, CancellationToken cancellationToken);
        Task SaveAsync(BotDataKey key, BotStoreType botStoreType, T data, CancellationToken cancellationToken);
        Task<bool> FlushAsync(BotDataKey key, CancellationToken cancellationToken);
    }

    public class InMemoryDataStore : IBotDataStore<BotData>
    {
        internal readonly ConcurrentDictionary<string, string> store = new ConcurrentDictionary<string, string>();
        private readonly Dictionary<BotStoreType, object> locks = new Dictionary<BotStoreType, object>()
        {
            { BotStoreType.BotConversationData, new object() },
            { BotStoreType.BotPrivateConversationData, new object() },
            { BotStoreType.BotUserData, new object() }
        };
        
        async Task<BotData> IBotDataStore<BotData>.LoadAsync(BotDataKey key, BotStoreType botStoreType, CancellationToken cancellationToken)
        {
            string serializedData;
            serializedData = store.GetOrAdd(GetKey(key, botStoreType),
                                            dictionaryKey => Serialize(new BotData { ETag = DateTime.UtcNow.Ticks.ToString() }));
            return Deserialize(serializedData);
        }

        async Task IBotDataStore<BotData>.SaveAsync(BotDataKey key, BotStoreType botStoreType, BotData botData, CancellationToken cancellationToken)
        {
            lock (locks[botStoreType])
            {
                store.AddOrUpdate(GetKey(key, botStoreType), Serialize(botData), (dictionaryKey, value) =>
                {
                    if (botData.ETag != "*" && Deserialize(value).ETag != botData.ETag)
                    {
                        throw new HttpException((int)HttpStatusCode.PreconditionFailed, "Inconsistent SaveAsync based on ETag!");
                    }
                    botData.ETag = DateTime.UtcNow.Ticks.ToString();
                    return Serialize(botData);
                });
            }
        }

        Task<bool> IBotDataStore<BotData>.FlushAsync(BotDataKey key, CancellationToken cancellationToken)
        {
            // Everything is saved. Flush is no-op
            return Task.FromResult(true);
        }

        private string GetKey(BotDataKey key, BotStoreType botStoreType)
        {
            switch (botStoreType)
            {
                case BotStoreType.BotConversationData:
                    return $"conversation:{key.BotId}:{key.ChannelId}:{key.ConversationId}";
                case BotStoreType.BotUserData:
                    return $"user:{key.BotId}:{key.ChannelId}:{key.UserId}";
                case BotStoreType.BotPrivateConversationData:
                    return $"privateConversation:{key.BotId}:{key.ChannelId}:{key.UserId}:{key.ConversationId}";
                default:
                    throw new ArgumentException("Unsupported bot store type!");
            }
        }

        private static string Serialize(BotData data)
        {
            using (var cmpStream = new MemoryStream())
            using (var stream = new GZipStream(cmpStream, CompressionMode.Compress))
            using (var streamWriter = new StreamWriter(stream))
            {
                var serializedJSon = JsonConvert.SerializeObject(data);
                streamWriter.Write(serializedJSon);
                streamWriter.Close();
                stream.Close();
                return Convert.ToBase64String(cmpStream.ToArray());
            }
        }

        private static BotData Deserialize(string str)
        {
            byte[] bytes = Convert.FromBase64String(str);
            using (var stream = new MemoryStream(bytes))
            using (var gz = new GZipStream(stream, CompressionMode.Decompress))
            using (var streamReader = new StreamReader(gz))
            {
                return JsonConvert.DeserializeObject<BotData>(streamReader.ReadToEnd());
            }
        }
    }

    public class ConnectorStore : IBotDataStore<BotData>
    {
        private readonly IStateClient stateClient; 
        public ConnectorStore(IStateClient stateClient)
        {
            SetField.NotNull(out this.stateClient, nameof(stateClient), stateClient);
        }

        async Task<BotData> IBotDataStore<BotData>.LoadAsync(BotDataKey key, BotStoreType botStoreType, CancellationToken cancellationToken)
        {
            BotData botData;
            switch(botStoreType)
            {
                case BotStoreType.BotConversationData:
                    botData = await stateClient.BotState.GetConversationDataAsync(key.ChannelId, key.ConversationId, cancellationToken);
                    break;
                case BotStoreType.BotUserData:
                    botData = await stateClient.BotState.GetUserDataAsync(key.ChannelId, key.UserId, cancellationToken);
                    break;
                case BotStoreType.BotPrivateConversationData:
                    botData = await stateClient.BotState.GetPrivateConversationDataAsync(key.ChannelId, key.ConversationId, key.UserId, cancellationToken);
                    break;
                default:
                    throw new ArgumentException($"{botStoreType} is not a valid store type!");
            }
            return botData; 
        }

        async Task IBotDataStore<BotData>.SaveAsync(BotDataKey key, BotStoreType botStoreType, BotData botData, CancellationToken cancellationToken)
        {
            switch(botStoreType)
            {
                case BotStoreType.BotConversationData:
                    await stateClient.BotState.SetConversationDataAsync(key.ChannelId, key.ConversationId, botData, cancellationToken);
                    break;
                case BotStoreType.BotUserData:
                    await stateClient.BotState.SetUserDataAsync(key.ChannelId, key.UserId, botData, cancellationToken);
                    break;
                case BotStoreType.BotPrivateConversationData:
                    await stateClient.BotState.SetPrivateConversationDataAsync(key.ChannelId, key.ConversationId, key.UserId, botData, cancellationToken);
                    break;
                default:
                    throw new ArgumentException($"{botStoreType} is not a valid store type!");
            }
        }

        Task<bool> IBotDataStore<BotData>.FlushAsync(BotDataKey key, CancellationToken cancellationToken)
        {
            // Everything is saved. Flush is no-op
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// The data consistency policy for <see cref="CachingBotDataStore"/>
    /// </summary>
    public enum CachingBotDataStoreConsistencyPolicy
    {
        /// <summary>
        /// Causes <see cref="CachingBotDataStore"/> to set <see cref="BotData.ETag"/> to "*" when it flushes the data to storage. 
        /// As a result last write will overwrite the data.
        /// </summary>
        LastWriteWins,
        /// <summary>
        /// Causes <see cref="CachingBotDataStore"/> to write data with the same <see cref="BotData.ETag"/> 
        /// returned by <see cref="CachingBotDataStore.inner"/>. As a result <see cref="IBotDataStore{T}.FlushAsync(BotDataKey, CancellationToken)"/>
        /// might fail because of ETag inconsistencies.
        /// </summary>
        ETagBasedConsistency
    }

    /// <summary>
    /// Caches data for <see cref="BotDataBase{T}"/> and wraps the data in <see cref="BotData"/> to be stored in <see cref="CachingBotDataStore.inner"/>
    /// </summary>
    public class CachingBotDataStore : IBotDataStore<BotData>
    {
        private readonly IBotDataStore<BotData> inner;
        internal readonly Dictionary<BotDataKey, CacheEntry> cache = new Dictionary<BotDataKey, CacheEntry>();
        private readonly CachingBotDataStoreConsistencyPolicy dataConsistencyPolicy;

        public CachingBotDataStore(IBotDataStore<BotData> inner, CachingBotDataStoreConsistencyPolicy dataConsistencyPolicy)
        {
            SetField.NotNull(out this.inner, nameof(inner), inner);
            this.dataConsistencyPolicy = dataConsistencyPolicy;
        }

        internal class CacheEntry
        {
            public BotData BotConversationData { set; get; }
            public BotData BotPrivateConversationData { set; get; }
            public BotData BotUserData { set; get;} 
        }

        async Task<bool> IBotDataStore<BotData>.FlushAsync(BotDataKey key, CancellationToken cancellationToken)
        {
            CacheEntry entry = default(CacheEntry);
            if (cache.TryGetValue(key, out entry))
            {
                // Removing the cached entry to make sure that we are not leaking 
                // flushed entries when CachingBotDataStore is registered as a singleton object.
                // Also since this store is not updating ETags on LoadAsync(...), there 
                // will be a conflict if we reuse the cached entries after flush. 
                cache.Remove(key);
                await this.Save(key, entry, cancellationToken);
                return true; 
            }
            else
            {
                return false;
            }
        }

        async Task<BotData> IBotDataStore<BotData>.LoadAsync(BotDataKey key, BotStoreType botStoreType, CancellationToken cancellationToken)
        {
            CacheEntry cacheEntry;
            BotData value = null;
            if (!cache.TryGetValue(key, out cacheEntry))
            {
                cacheEntry = new CacheEntry();
                cache.Add(key, cacheEntry);
                value = await LoadFromInnerAndCache(cacheEntry, botStoreType, key, cancellationToken);
            }
            else
            {
                switch (botStoreType)
                {
                    case BotStoreType.BotConversationData:
                        if (cacheEntry.BotConversationData != null)
                        {
                            value = cacheEntry.BotConversationData;
                        }
                        break;
                    case BotStoreType.BotPrivateConversationData:
                        if (cacheEntry.BotPrivateConversationData != null)
                        {
                            value = cacheEntry.BotPrivateConversationData;
                        }
                        break;
                    case BotStoreType.BotUserData:
                        if (cacheEntry.BotUserData != null)
                        {
                            value = cacheEntry.BotUserData;
                        }
                        break;
                }

                if(value == null)
                {
                    value = await LoadFromInnerAndCache(cacheEntry, botStoreType, key, cancellationToken);
                }
            }

            return value;
        }

        async Task IBotDataStore<BotData>.SaveAsync(BotDataKey key, BotStoreType botStoreType, BotData value, CancellationToken cancellationToken)
        {
            CacheEntry entry;
            if (!cache.TryGetValue(key, out entry))
            {
                entry = new CacheEntry();
                cache.Add(key, entry);
            }

            SetCachedValue(entry, botStoreType, value);
        }

        private async Task<BotData> LoadFromInnerAndCache(CacheEntry cacheEntry, BotStoreType botStoreType, BotDataKey key, CancellationToken token)
        {
            var value = await inner.LoadAsync(key, botStoreType, token);

            if (value != null)
            {
                SetCachedValue(cacheEntry, botStoreType, value);
            }
            else
            {
                // inner store returned null, we create a new instance of BotData with ETag = "*"
                value = new BotData() { ETag = "*" };
                SetCachedValue(cacheEntry, botStoreType, value);
            }
            return value; 
        }

        private void SetCachedValue(CacheEntry entry, BotStoreType botStoreType, BotData value)
        {
            switch (botStoreType)
            {
                case BotStoreType.BotConversationData:
                    entry.BotConversationData = value;
                    break;
                case BotStoreType.BotPrivateConversationData:
                    entry.BotPrivateConversationData = value;
                    break;
                case BotStoreType.BotUserData:
                    entry.BotUserData = value;
                    break;
            }
        }

        private async Task Save(BotDataKey key, CacheEntry entry, CancellationToken cancellationToken)
        {
            var tasks = new List<Task>();

            switch(this.dataConsistencyPolicy)
            {
                case CachingBotDataStoreConsistencyPolicy.LastWriteWins:
                    if (entry?.BotConversationData != null)
                    {
                        entry.BotConversationData.ETag = "*";
                    }

                    if (entry?.BotUserData != null)
                    {
                        entry.BotUserData.ETag = "*";
                    }

                    if (entry?.BotPrivateConversationData != null)
                    {
                        entry.BotPrivateConversationData.ETag = "*";
                    }
                    break;
                case CachingBotDataStoreConsistencyPolicy.ETagBasedConsistency:
                    // no action needed, store relies on the ETags returned by inner store
                    break;
                default:
                    throw new ArgumentException($"{this.dataConsistencyPolicy} is not a valid consistency policy!");
            }
            
            if (entry?.BotConversationData != null)
            {
                tasks.Add(inner.SaveAsync(key, BotStoreType.BotConversationData, entry.BotConversationData, cancellationToken));
            }

            if (entry?.BotUserData != null)
            {
                tasks.Add(inner.SaveAsync(key, BotStoreType.BotUserData, entry.BotUserData, cancellationToken));
            }

            if (entry?.BotPrivateConversationData != null)
            {
                tasks.Add(inner.SaveAsync(key, BotStoreType.BotPrivateConversationData, entry.BotPrivateConversationData, cancellationToken));
            }
            await Task.WhenAll(tasks);
        }
    }

    public abstract class BotDataBase<T> : IBotData
    {
        protected readonly IBotDataStore<BotData> botDataStore;
        protected readonly BotDataKey botDataKey;
        private IBotDataBag conversationData;
        private IBotDataBag privateConversationData;
        private IBotDataBag userData;

        public BotDataBase(IBotIdResolver botIdResolver, IMessageActivity message, IBotDataStore<BotData> botDataStore)
        {
            SetField.NotNull(out this.botDataStore, nameof(BotDataBase<T>.botDataStore), botDataStore);
            SetField.CheckNull(nameof(message), message);
            this.botDataKey = message.GetBotDataKey(botIdResolver.BotId); 
        }

        protected abstract T MakeData();
        protected abstract IBotDataBag WrapData(T data);

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            var conversationTask = LoadData(BotStoreType.BotConversationData, cancellationToken);
            var privateConversationTask = LoadData(BotStoreType.BotPrivateConversationData, cancellationToken);
            var userTask = LoadData(BotStoreType.BotUserData, cancellationToken);

            this.conversationData = await conversationTask;
            this.privateConversationData = await privateConversationTask;
            this.userData = await userTask; 
        }

        public async Task FlushAsync(CancellationToken cancellationToken)
        {
            await this.botDataStore.FlushAsync(botDataKey, cancellationToken);
        }

        IBotDataBag IBotData.ConversationData
        {
            get
            {
                CheckNull(nameof(conversationData), conversationData);
                return this.conversationData;
            }
        }

        IBotDataBag IBotData.PrivateConversationData
        {
            get
            {
                CheckNull(nameof(privateConversationData), privateConversationData);
                return this.privateConversationData;
            }
        }

        IBotDataBag IBotData.UserData
        {
            get
            {
                CheckNull(nameof(userData), userData);
                return this.userData;
            }
        }

        private async Task<IBotDataBag> LoadData(BotStoreType botStoreType, CancellationToken cancellationToken)
        {
            var botData = await this.botDataStore.LoadAsync(botDataKey, botStoreType, cancellationToken);
            if (botData?.Data == null)
            {
                botData.Data = this.MakeData();
                await this.botDataStore.SaveAsync(botDataKey, botStoreType, botData, cancellationToken);
            }
            return this.WrapData((T)botData.Data);
        }

        private void CheckNull(string name, IBotDataBag value)
        {
            if (value == null)
            {
                throw new InvalidOperationException($"{name} cannot be null! probably forgot to call LoadAsync() first!");
            }
        }
    }

    public sealed class DictionaryBotData : BotDataBase<Dictionary<string, object>>
    {
        public DictionaryBotData(IBotIdResolver botIdResolver, IMessageActivity message, IBotDataStore<BotData> botDataStore)
            : base(botIdResolver, message, botDataStore)
        {
        }

        protected override Dictionary<string, object> MakeData()
        {
            return new Dictionary<string, object>();
        }

        private sealed class Bag : IBotDataBag
        {
            private readonly Dictionary<string, object> bag;
            public Bag(Dictionary<string, object> bag)
            {
                SetField.NotNull(out this.bag, nameof(bag), bag);
            }

            int IBotDataBag.Count { get { return this.bag.Count; } }

            void IBotDataBag.SetValue<T>(string key, T value)
            {
                this.bag[key] = value;
            }

            bool IBotDataBag.TryGetValue<T>(string key, out T value)
            {
                object boxed;
                bool found = this.bag.TryGetValue(key, out boxed);
                if (found)
                {
                    if (boxed is T)
                    {
                        value = (T)boxed;
                        return true;
                    }
                }

                value = default(T);
                return false;
            }

            bool IBotDataBag.RemoveValue(string key)
            {
                return this.bag.Remove(key);
            }

            void IBotDataBag.Clear()
            {
                this.bag.Clear();
            }
        }

        protected override IBotDataBag WrapData(Dictionary<string, object> data)
        {
            return new Bag(data);
        }
    }

    public sealed class JObjectBotData : BotDataBase<JObject>
    {
        public JObjectBotData(IBotIdResolver botIdResolver, IMessageActivity message, IBotDataStore<BotData> botDataStore)
            : base(botIdResolver, message, botDataStore)
        {
        }

        protected override JObject MakeData()
        {
            return new JObject();
        }
        private sealed class Bag : IBotDataBag
        {
            private readonly JObject bag;
            public Bag(JObject bag)
            {
                SetField.NotNull(out this.bag, nameof(bag), bag);
            }

            int IBotDataBag.Count { get { return this.bag.Count; } }

            void IBotDataBag.SetValue<T>(string key, T value)
            {
                var token = JToken.FromObject(value);
#if DEBUG
                var copy = token.ToObject<T>();
#endif
                this.bag[key] = token;
            }

            bool IBotDataBag.TryGetValue<T>(string key, out T value)
            {
                JToken token;
                bool found = this.bag.TryGetValue(key, out token);
                if (found)
                {
                    value = token.ToObject<T>();
                    return true;
                }

                value = default(T);
                return false;
            }

            bool IBotDataBag.RemoveValue(string key)
            {
                return this.bag.Remove(key);
            }

            void IBotDataBag.Clear()
            {
                this.bag.RemoveAll();
            }

        }

        protected override IBotDataBag WrapData(JObject data)
        {
            return new Bag(data);
        }
    }

    public sealed class BotDataBagStream : MemoryStream
    {
        private readonly IBotDataBag bag;
        private readonly string key;
        public BotDataBagStream(IBotDataBag bag, string key)
        {
            SetField.NotNull(out this.bag, nameof(bag), bag);
            SetField.NotNull(out this.key, nameof(key), key);

            byte[] blob;
            if (this.bag.TryGetValue(key, out blob))
            {
                this.Write(blob, 0, blob.Length);
                this.Position = 0;
            }
        }

        public override void Flush()
        {
            base.Flush();

            var blob = this.ToArray();
            this.bag.SetValue(this.key, blob);
        }

        public override void Close()
        {
            this.Flush();
            base.Close();
        }
    }

    public interface IBotIdResolver
    {
        string BotId { get; }
    }

    public sealed class BotIdResolver : IBotIdResolver
    {
        private readonly string botId;

        public string BotId { get { return botId; } }

        public BotIdResolver(string botId = null)
        {
           SetField.NotNull(out this.botId, nameof(botId), botId ?? ConfigurationManager.AppSettings["BotId"] ?? ConfigurationManager.AppSettings["MicrosoftAppId"]);
        }
    }

    public static partial class Extensions
    {
        public static BotDataKey GetBotDataKey(this IMessageActivity message, string botId)
        {
            SetField.CheckNull(nameof(botId), botId);
            return new BotDataKey
            {
                BotId =  botId,
                UserId = message.From.Id,
                ConversationId = message.Conversation.Id,
                ChannelId = message.ChannelId
            };
        }
    }
}
