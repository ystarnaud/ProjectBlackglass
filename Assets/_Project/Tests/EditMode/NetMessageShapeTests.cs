using System;
using System.Linq;
using System.Reflection;
using FishNet.Broadcast;
using FishNet.Object.Synchronizing;
using NUnit.Framework;
using UnityEngine;

namespace Blackglass.Tests
{
    /// <summary>
    /// No normal client message can carry the seed, a spawn plan, a mission plan or a layout (spec section 3.8): every field of
    /// every broadcast and every replicated avatar field is a plain value, and none is named after one of those.
    /// </summary>
    public class NetMessageShapeTests
    {
        static readonly string[] ForbiddenNames = { "seed", "plan", "spawn", "layout" };

        static Type[] Broadcasts() => typeof(NetHelloMessage).Assembly.GetTypes()
            .Where(t => t.IsValueType && typeof(IBroadcast).IsAssignableFrom(t)).ToArray();

        static bool IsPlain(Type type) =>
            type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(Vector3) || type == typeof(Quaternion);

        static void CheckName(string name, string where)
        {
            foreach (var bad in ForbiddenNames)
                Assert.That(name.IndexOf(bad, StringComparison.OrdinalIgnoreCase), Is.LessThan(0), where + " is named after '" + bad + "'");
        }

        static void CheckFields(Type type, string path)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var where = path + "." + field.Name;
                CheckName(field.Name, where);
                var fieldType = field.FieldType;
                if (fieldType.IsArray)
                {
                    var element = fieldType.GetElementType();
                    Assert.That(element.IsValueType && !element.IsPrimitive && !element.IsEnum && element.Namespace == "Blackglass", Is.True,
                        where + " is an array of " + element.Name + "; only arrays of the networking experiment's own structs are allowed");
                    CheckFields(element, where + "[]");
                    continue;
                }
                Assert.That(IsPlain(fieldType), Is.True, where + " has type " + fieldType.Name);
            }
        }

        [Test]
        public void TheBroadcasts_AreExactlyTheKnownMessages()
        {
            Assert.That(Broadcasts().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal), Is.EqualTo(new[]
            {
                "NetContainerMessage", "NetFixtureMessage", "NetFixtureStepMessage", "NetHelloMessage", "NetKnowledgeMessage",
                "NetRefusedMessage", "NetRequestMessage", "NetResultMessage", "NetWelcomeMessage",
            }));
        }

        [Test]
        public void EveryBroadcastField_IsAPlainValue_NamedAfterNoSeedPlanSpawnOrLayout()
        {
            foreach (var type in Broadcasts())
                CheckFields(type, type.Name);
        }

        [Test]
        public void EveryReplicatedAvatarField_IsAPlainValue_NamedAfterNoSeedPlanSpawnOrLayout()
        {
            var fields = typeof(NetUnitState).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(SyncVar<>)).ToList();
            Assert.That(fields.Select(f => f.Name), Is.SupersetOf(new[] { "unitId", "hostile", "health", "maxHealth", "alive", "order", "queue", "steering" }));
            foreach (var field in fields)
            {
                CheckName(field.Name, "NetUnitState." + field.Name);
                var valueType = field.FieldType.GetGenericArguments()[0];
                Assert.That(IsPlain(valueType), Is.True, "NetUnitState." + field.Name + " replicates a " + valueType.Name);
            }
        }
    }
}
