//▼ ADD 2024.01.17 Fixture@Test 夹具：03 §5 を逐字採用
using System;

namespace Assets.Common.Script.Base
{
    public abstract class Enumeration : IComparable
    {
        public string Name { get; private set; }

        public int Id { get; private set; }

        protected Enumeration(int id, string name) => (Id, Name) = (id, name);

        public override bool Equals(object obj)
        {
            Enumeration other = obj as Enumeration;
            if (other == null)
            {
                return false;
            }

            return GetType().Equals(other.GetType()) && Id.Equals(other.Id);
        }

        public override int GetHashCode() => Id.GetHashCode();

        public int CompareTo(object other) => Id.CompareTo(((Enumeration)other).Id);

        public static bool operator ==(Enumeration left, Enumeration right)
        {
            if (ReferenceEquals(left, null))
            {
                return ReferenceEquals(right, null);
            }

            return left.Equals(right);
        }

        public static bool operator !=(Enumeration left, Enumeration right) => !(left == right);
    }
}
//▲ ADD 2024.01.17 Fixture@Test 夹具
