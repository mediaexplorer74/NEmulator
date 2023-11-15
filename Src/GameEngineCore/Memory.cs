using System;
//using System.Buffers;
using System.ComponentModel;

namespace csPixelGameEngineCore
{
    //
    // Сводка:
    //     Represents a contiguous region of memory.
    //
    // Параметры типа:
    //   T:
    //     The type of items in the System.Memory`1.
    public readonly struct Memory<T> : IEquatable<Memory<T>>
    {
        //
        // Сводка:
        //     Creates a new System.Memory`1 object over the entirety of a specified array.
        //
        //
        // Параметры:
        //   array:
        //     The array from which to create the System.Memory`1 object.
        //
        // Исключения:
        //   T:System.ArrayTypeMismatchException:
        //     T is a reference type, and array is not an array of type T. -or- The array is
        //     covariant.
        public Memory(T[] array)
        {
            
        }
        //
        // Сводка:
        //     Creates a new System.Memory`1 object that includes a specified number of elements
        //     of an array beginning at a specified index.
        //
        // Параметры:
        //   array:
        //     The source array.
        //
        //   start:
        //     The index of the first element to include in the new System.Memory`1.
        //
        //   length:
        //     The number of elements to include in the new System.Memory`1.
        //
        // Исключения:
        //   T:System.ArgumentOutOfRangeException:
        //     array is null, but start or length is non-zero. -or- start is outside the bounds
        //     of the array. -or- start and length exceeds the number of elements in the array.
        //
        //
        //   T:System.ArrayTypeMismatchException:
        //     T is a reference type, and array is not an array of type T.
        public Memory(T[] array, int start, int length)
        { 
        }

        //
        // Сводка:
        //     Returns an empty System.Memory`1 object.
        //
        // Возврат:
        //     An empty object.
        public static Memory<T> Empty { get; }
        //
        // Сводка:
        //     Indicates whether the current instance is empty.
        //
        // Возврат:
        //     true if the current instance is empty; otherwise, false.
        public bool IsEmpty { get; }
        //
        // Сводка:
        //     Gets the number of items in the current instance.
        //
        // Возврат:
        //     The number of items in the current instance.
        public int Length { get; }
        //
        // Сводка:
        //     Returns a span from the current instance.
        //
        // Возврат:
        //     A span created from the current System.Memory`1 object.
        public Span<T> Span { get; }

        //
        // Сводка:
        //     Copies the contents of a System.Memory`1 object into a destination System.Memory`1
        //     object.
        //
        // Параметры:
        //   destination:
        //     The destination System.Memory`1 object.
        //
        // Исключения:
        //   T:System.ArgumentException:
        //     The length of destination is less than the length of the current instance.
        public void CopyTo(Memory<T> destination)
        { 
        }
        //
        // Сводка:
        //     Determines whether the specified object is equal to the current object.
        //
        // Параметры:
        //   obj:
        //     The object to compare with the current instance.
        //
        // Возврат:
        //     true if the current instance and obj are equal; otherwise, false.
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool Equals(object obj)
        {
            return default;
        }

        //
        // Сводка:
        //     Determines whether the specified System.Memory`1 object is equal to the current
        //     object.
        //
        // Параметры:
        //   other:
        //     The object to compare with the current instance.
        //
        // Возврат:
        //     true if the current instance and other are equal; otherwise, false.
        public bool Equals(Memory<T> other)
        {
            return default;
        }

        //
        // Сводка:
        //     Returns the hash code for this instance.
        //
        // Возврат:
        //     A 32-bit signed integer hash code.
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override int GetHashCode()
        {
            return default;
        }

        //
        // Сводка:
        //     Creates a handle for the System.Memory`1 object.
        //
        // Возврат:
        //     A handle for the System.Memory`1 object.
        //
        // Исключения:
        //   T:System.ArgumentException:
        //     An instance with non-primitive (non-blittable) members cannot be pinned.
        public MemoryHandle Pin()
        {
            return default;
        }

        //
        // Сводка:
        //     Forms a slice out of the current memory that begins at a specified index.
        //
        // Параметры:
        //   start:
        //     The index at which to begin the slice.
        //
        // Возврат:
        //     An object that contains all elements of the current instance from start to the
        //     end of the instance.
        //
        // Исключения:
        //   T:System.ArgumentOutOfRangeException:
        //     start is less than zero or greater than System.Memory`1.Length.
        public Memory<T> Slice(int start)
        {
            return default;
        }

        //
        // Сводка:
        //     Forms a slice out of the current memory starting at a specified index for a specified
        //     length.
        //
        // Параметры:
        //   start:
        //     The index at which to begin the slice.
        //
        //   length:
        //     The number of elements to include in the slice.
        //
        // Возврат:
        //     An object that contains length elements from the current instance starting at
        //     start.
        //
        // Исключения:
        //   T:System.ArgumentOutOfRangeException:
        //     start is less than zero or greater than System.Memory`1.Length. -or- length is
        //     greater than System.Memory`1.Length - start
        public Memory<T> Slice(int start, int length)
        {
            return default;
        }

        //
        // Сводка:
        //     Copies the contents from the memory into a new array.
        //
        // Возврат:
        //     An array containing the elements in the current memory.
        public T[] ToArray()
        {
            return default;
        }

        //
        // Сводка:
        //     Returns the string representation of this System.Memory`1 object.
        //
        // Возврат:
        //     the string representation of this System.Memory`1 object.
        public override string ToString()
        {
            return default;
        }
        //
        // Сводка:
        //     Copies the contents of the memory into a destination System.Memory`1 instance.
        //
        //
        // Параметры:
        //   destination:
        //     The destination System.Memory`1 object.
        //
        // Возврат:
        //     true if the copy operation succeeds; otherwise, false.
        public bool TryCopyTo(Memory<T> destination)
        {
            return default;
        }

        //
        // Сводка:
        //     Defines an implicit conversion of an System.ArraySegment`1 object to a System.Memory`1
        //     object.
        //
        // Параметры:
        //   segment:
        //     The object to convert.
        //
        // Возврат:
        //     The converted System.ArraySegment`1 object.
        public static implicit operator Memory<T>(ArraySegment<T> segment)
        {
            return default;
        }
        //
        // Сводка:
        //     Defines an implicit conversion of a System.Memory`1 object to a System.ReadOnlyMemory`1
        //     object.
        //
        // Параметры:
        //   memory:
        //     The object to convert.
        //
        // Возврат:
        //     The converted object.
        public static implicit operator ReadOnlyMemory<T>(Memory<T> memory)
        {
            return default;
        }

        public static implicit operator Memory<T>(T[] array)
        {
            return default;
        }
    }
}