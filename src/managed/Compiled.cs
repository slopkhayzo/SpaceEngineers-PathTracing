using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SpaceEngineers.RTHWAccel
{
    // Compiled access to the game's non-public members: expression trees
    // compile to dynamic methods that skip visibility checks, so a member read
    // costs a delegate call instead of a reflection call. Paths are dotted
    // member names, each a field or property of the previous member's type
    // (base types and interfaces included); a miss throws with the full name.
    internal static class Compiled
    {
        // o => ((root)o).path, converted to T.
        public static Func<object, T> Getter<T>(Type root, string path)
        {
            ParameterExpression input = Expression.Parameter(typeof(object), "o");
            Expression body = To<T>(Path(Expression.Convert(input, root), path));
            return Expression.Lambda<Func<object, T>>(body, input).Compile();
        }

        // () => root.path, the first member static.
        public static Func<T> StaticGetter<T>(Type root, string path)
        {
            string[] names = path.Split('.');
            Expression body = StaticMember(root, names[0]);
            foreach (string name in names.Skip(1))
                body = Member(body, name);
            return Expression.Lambda<Func<T>>(To<T>(body)).Compile();
        }

        public static Expression Path(Expression start, string path)
        {
            Expression body = start;
            foreach (string name in path.Split('.'))
                body = Member(body, name);
            return body;
        }

        public static Expression To<T>(Expression body) =>
            body.Type == typeof(T) ? body : Expression.Convert(body, typeof(T));

        public static Expression Member(Expression target, string name)
        {
            for (Type type = target.Type; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, GameReflection.All);
                if (field != null)
                    return Expression.Field(target, field);
                PropertyInfo property = type.GetProperty(name, GameReflection.All);
                if (property != null)
                    return Expression.Property(target, property);
            }
            foreach (Type contract in target.Type.GetInterfaces())
            {
                PropertyInfo property = contract.GetProperty(name, GameReflection.All);
                if (property != null)
                    return Expression.Property(Expression.Convert(target, contract), property);
            }
            throw new DetourException($"member {target.Type.FullName}::{name} not found");
        }

        public static Expression StaticMember(Type type, string name)
        {
            FieldInfo field = type.GetField(name, GameReflection.All);
            if (field != null && field.IsStatic)
                return Expression.Field(null, field);
            PropertyInfo property = type.GetProperty(name, GameReflection.All);
            if (property != null && property.GetMethod != null && property.GetMethod.IsStatic)
                return Expression.Property(null, property);
            throw new DetourException($"static member {type.FullName}::{name} not found");
        }
    }
}
