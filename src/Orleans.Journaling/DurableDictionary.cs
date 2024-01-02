using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Session;

namespace Orleans.Journaling;

public class DurableDictionary<K, V>(IFieldCodec<K> keyCodec, IFieldCodec<V> valueCodec, SerializerSessionPool serializerSessionPool) : IDictionary<K, V>, IDurableStateMachine where K : notnull
{
    private readonly SerializerSessionPool _serializerSessionPool = serializerSessionPool;
    private readonly IFieldCodec<K> _keyCodec = keyCodec;
    private readonly IFieldCodec<V> _valueCodec = valueCodec;
    private const byte VersionByte = 0;
    private readonly Dictionary<K, V> _items = [];
    private IStateMachineLogWriter? _storage;

    public V this[K key]
    {
        get => _items[key];

        set
        {
            ApplySet(key, value);
            AppendSet(key, value);
        }
    }

    public int Count => _items.Count;

    public ICollection<K> Keys => _items.Keys;

    public ICollection<V> Values => _items.Values;

    public bool IsReadOnly => ((ICollection<KeyValuePair<K, V>>)_items).IsReadOnly;

    void IDurableStateMachine.Reset(IStateMachineLogWriter storage)
    {
        _items.Clear();
        _storage = storage;
    }

    void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
    {
        using var session = _serializerSessionPool.GetSession();
        var reader = Reader.Create(logEntry, session);
        var version = reader.ReadByte();
        if (version != VersionByte)
        {
            throw new NotSupportedException($"This instance of {nameof(DurableDictionary<K, V>)} supports version {(uint)VersionByte} and not version {(uint)version}.");
        }

        var commandType = (CommandType)reader.ReadVarUInt32();
        switch (commandType)
        {
            case CommandType.Set:
                ApplySet(ReadKey(ref reader), ReadValue(ref reader));
                break;
            case CommandType.Remove:
                ApplyRemove(ReadKey(ref reader));
                break;
            case CommandType.Clear:
                ApplyClear();
                break;
            case CommandType.Snapshot:
                ApplySnapshot(ref reader);
                break;
            default:
                throw new NotSupportedException($"Command type {commandType} is not supported");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        K ReadKey(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _keyCodec.ReadValue(ref reader, field);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        V ReadValue(ref Reader<ReadOnlySequenceInput> reader)
        {
            var field = reader.ReadFieldHeader();
            return _valueCodec.ReadValue(ref reader, field);
        }

        void ApplySnapshot(ref Reader<ReadOnlySequenceInput> reader)
        {
            var count = reader.ReadVarInt32();
            _items.Clear();
            _items.EnsureCapacity(count);
            for (var i = 0; i < count; i++)
            {
                var key = ReadKey(ref reader);
                var value = ReadValue(ref reader);
                ApplySet(key, value);
            }
        }
    }

    void IDurableStateMachine.AppendEntries(StateMachineStorageWriter logWriter)
    {
        // This state machine implementation appends log entries as the data structure is modified, so there is no need to perform separate writing here.
    }

    void IDurableStateMachine.AppendSnapshot(StateMachineStorageWriter snapshotWriter)
    {
        snapshotWriter.AppendEntry(static (self, bufferWriter) =>
        {
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Snapshot);
            writer.WriteVarInt32(self._items.Count);
            foreach (var (key, value) in self._items)
            {
                self._keyCodec.WriteField(ref writer, 0, typeof(K), key);
                self._valueCodec.WriteField(ref writer, 0, typeof(V), value);
            }

            writer.Commit();
        }, this);
    }

    public void Clear()
    {
        ApplyClear();
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            using var session = state._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Clear);
            writer.Commit();
        },
        this);
    }

    public bool Contains(K key) => _items.ContainsKey(key);

    public bool Remove(K key)
    {
        if (ApplyRemove(key))
        {
            AppendRemove(key);
            return true;
        }

        return false;
    }

    private void AppendRemove(K key)
    {
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, key) = state;
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Remove);
            self._keyCodec.WriteField(ref writer, 0, typeof(K), key);
            writer.Commit();
        }, (this, key));
    }

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private void AppendSet(K key, V value)
    {
        GetStorage().AppendEntry(static (state, bufferWriter) =>
        {
            var (self, key, value) = state;
            using var session = self._serializerSessionPool.GetSession();
            var writer = Writer.Create(bufferWriter, session);
            writer.WriteByte(VersionByte);
            writer.WriteVarUInt32((uint)CommandType.Set);
            self._keyCodec.WriteField(ref writer, 0, typeof(K), key);
            self._valueCodec.WriteField(ref writer, 1, typeof(V), value);
            writer.Commit();
        },
        (this, key, value));
    }

    protected virtual void OnSet(K key, V value) { }

    private void ApplySet(K key, V value)
    {
        _items[key] = value;
        OnSet(key, value);
    }

    private bool ApplyRemove(K key) => _items.Remove(key);
    private void ApplyClear() => _items.Clear();

    [DoesNotReturn]
    private static void ThrowIndexOutOfRange() => throw new ArgumentOutOfRangeException("index", "Index was out of range. Must be non-negative and less than the size of the collection");

    private IStateMachineLogWriter GetStorage()
    {
        Debug.Assert(_storage is not null);
        return _storage;
    }

    public IDurableStateMachine DeepCopy() => throw new NotImplementedException();
    public void Add(K key, V value)
    {
        _items.Add(key, value);
        OnSet(key, value);
        AppendSet(key, value);
    }

    public bool ContainsKey(K key) => _items.ContainsKey(key);
    public bool TryGetValue(K key, [MaybeNullWhen(false)] out V value) => _items.TryGetValue(key, out value);
    public void Add(KeyValuePair<K, V> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<K, V> item) => _items.Contains(item);
    public void CopyTo(KeyValuePair<K, V>[] array, int arrayIndex) => ((ICollection<KeyValuePair<K, V>>)_items).CopyTo(array, arrayIndex);
    public bool Remove(KeyValuePair<K, V> item)
    {
        if (((ICollection<KeyValuePair<K, V>>)_items).Remove(item))
        {
            AppendRemove(item.Key);
            return true;
        }

        return false;
    }

    public IEnumerator<KeyValuePair<K, V>> GetEnumerator() => ((IEnumerable<KeyValuePair<K, V>>)_items).GetEnumerator();

    private enum CommandType
    {
        Set = 0,
        Remove = 1,
        Clear = 2,
        Snapshot = 3 
    }
}
