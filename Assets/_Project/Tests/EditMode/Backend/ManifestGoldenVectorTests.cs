using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Blackglass.Backend.Core;
using NUnit.Framework;

namespace Blackglass.Tests
{
    /// <summary>
    /// The manifest-v1 golden vectors (Backend/contracts/manifest-v1, the authority), read as raw bytes: every valid input
    /// canonicalizes to its exact bytes and hash, every invalid input is refused with its code, and the folder itself is pinned
    /// by its file set and hashes as of 6bdc6ca, so a changed vector fails here instead of being silently accepted.
    /// </summary>
    public sealed class ManifestGoldenVectorTests
    {
        static string Folder => Path.Combine(ContractVectors.ProjectRoot, "Backend", "contracts", "manifest-v1");

        static string Trimmed(string path)
        {
            var text = File.ReadAllText(path, new UTF8Encoding(false));
            Assert.That(text.EndsWith("\n", StringComparison.Ordinal), Is.True, path + " ends with one newline");
            return text.Substring(0, text.Length - 1);
        }

        [Test]
        public void Every_valid_vector_canonicalizes_to_its_bytes_and_hash()
        {
            var inputs = Directory.GetFiles(Path.Combine(Folder, "valid"), "*.input.json").OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.That(inputs.Count, Is.GreaterThanOrEqualTo(13));
            foreach (var input in inputs)
            {
                var stem = input.Substring(0, input.Length - ".input.json".Length);
                var result = ManifestCodec.Parse(File.ReadAllBytes(input));
                Assert.That(result.Ok, Is.True, input + " refused with " + result.Code);
                Assert.That(result.CanonicalBytes, Is.EqualTo(File.ReadAllBytes(stem + ".canonical.json")), input);
                Assert.That(result.ManifestHash, Is.EqualTo(Trimmed(stem + ".hash.txt")), input);
                var again = ManifestCodec.Parse(result.CanonicalBytes);
                Assert.That(again.CanonicalBytes, Is.EqualTo(result.CanonicalBytes), input + " is a fixed point");
                Assert.That(ManifestCodec.ToCanonicalBytes(result.Manifest), Is.EqualTo(result.CanonicalBytes), input);
            }
        }

        [Test]
        public void Every_invalid_vector_is_refused_with_its_code()
        {
            var inputs = Directory.GetFiles(Path.Combine(Folder, "invalid"), "*.input.json").OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.That(inputs.Count, Is.GreaterThanOrEqualTo(20));
            var seen = new HashSet<string>();
            foreach (var input in inputs)
            {
                var expected = Trimmed(input.Substring(0, input.Length - ".input.json".Length) + ".code.txt");
                var result = ManifestCodec.Parse(File.ReadAllBytes(input));
                Assert.That(result.Ok, Is.False, input);
                Assert.That(result.Code, Is.EqualTo(expected), input);
                seen.Add(result.Code);
            }
            Assert.That(seen, Is.EquivalentTo(ManifestCodes.All.Where(c => c != ManifestCodes.TooLarge)));
        }

        [Test]
        public void Vectors_10_11_12_share_02s_canonical_bytes()
        {
            var full = File.ReadAllBytes(Path.Combine(Folder, "valid", "02-full.canonical.json"));
            foreach (var name in new[] { "10-reverse-lists", "11-whitespace", "12-key-order" })
            {
                Assert.That(File.ReadAllBytes(Path.Combine(Folder, "valid", name + ".canonical.json")), Is.EqualTo(full), name);
                Assert.That(ManifestCodec.Parse(File.ReadAllBytes(Path.Combine(Folder, "valid", name + ".input.json"))).CanonicalBytes, Is.EqualTo(full), name);
            }
        }

        [Test]
        public void A_body_over_65536_bytes_is_TooLarge()
        {
            var full = File.ReadAllBytes(Path.Combine(Folder, "valid", "02-full.canonical.json"));
            Assert.That(ManifestCodec.Parse(Padded(full, ResultManifestV1.MaxBytes + 1)).Code, Is.EqualTo(ManifestCodes.TooLarge));
            Assert.That(ManifestCodec.Parse(Padded(full, ResultManifestV1.MaxBytes)).Ok, Is.True, "65,536 bytes is still allowed");
        }

        /// <summary>The manifest with spaces before its final closing brace, <paramref name="total"/> bytes long.</summary>
        static byte[] Padded(byte[] manifest, int total)
        {
            var padded = new byte[total];
            Array.Copy(manifest, padded, manifest.Length - 1);
            for (var i = manifest.Length - 1; i < total - 1; i++) padded[i] = (byte)' ';
            padded[total - 1] = (byte)'}';
            return padded;
        }

        [Test]
        public void The_vector_folder_is_unchanged()
        {
            var files = Directory.GetFiles(Folder, "*", SearchOption.AllDirectories)
                .Select(p => p.Substring(Folder.Length + 1).Replace(Path.DirectorySeparatorChar, '/')).OrderBy(p => p, StringComparer.Ordinal).ToList();
            Assert.That(files, Is.EqualTo(PinnedAt6bdc6ca.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList()));
            using (var sha = SHA256.Create())
                foreach (var pair in PinnedAt6bdc6ca)
                {
                    var hex = string.Concat(sha.ComputeHash(File.ReadAllBytes(Path.Combine(Folder, pair.Key))).Select(b => b.ToString("x2")));
                    Assert.That(hex, Is.EqualTo(pair.Value), pair.Key);
                }
        }

        /// <summary>SHA-256 of every file, from `git show 6bdc6ca:Backend/contracts/manifest-v1/&lt;path&gt; | sha256sum`.</summary>
        static readonly Dictionary<string, string> PinnedAt6bdc6ca = new Dictionary<string, string>
        {
            { ".gitattributes", "d674937c0d6bfb9b7f1362797bebcb0706ff463a16912124a78b39ddff431525" },
            { "README.md", "3130ad4705d439cef0a08160aa0c1d796b1a8fa9a32638695609a2769f1d5740" },
            { "invalid/01-not-json-trailing-comma.code.txt", "6d66481afd9a652e288adced1043a8c243e92a7acb5171582eb51ed37ccad0b2" },
            { "invalid/01-not-json-trailing-comma.input.json", "bfe2f37460b27b72cb41754bdb780c4e0ff2ab8a8b5f38ddf87ee2fdbf36d16f" },
            { "invalid/02-not-json-leading-zero.code.txt", "6d66481afd9a652e288adced1043a8c243e92a7acb5171582eb51ed37ccad0b2" },
            { "invalid/02-not-json-leading-zero.input.json", "595f5bb0ac987ac4bebc70f82f6be7181403d41cc4c60000369c7680bf151f8e" },
            { "invalid/03-too-deep.code.txt", "ccb8e52ea4febdfdd75fabd3e7e87817c34aede309f65adfcffc3978ee6032aa" },
            { "invalid/03-too-deep.input.json", "faa9526301fa0318ae430a9ffee38a0760a58b38aae366db66420b870358d899" },
            { "invalid/04-wrong-type.code.txt", "640a623fe2ee58722164f7f50619636e67815862e8cd030f6cdf6bf52d1d6697" },
            { "invalid/04-wrong-type.input.json", "96b7f5c64e4ccf071de36d66fa2943dbf548807a0f4c72232d1f6653f1c288df" },
            { "invalid/05-duplicate-key.code.txt", "c08490f39382051cb53df3a9dc6a198e80fad6fa0a9d23ab283d92d8b0af0687" },
            { "invalid/05-duplicate-key.input.json", "064740ce22b5120098cf58325cf1525098e58ed3b8ae351cfd79c869b1759525" },
            { "invalid/06-unknown-member.code.txt", "6a1110728c850e141fcc0ba5578a73b1923083ae6755ae4360bfc44bc48b0b6b" },
            { "invalid/06-unknown-member.input.json", "6d965dd5b552d046a5f7ce1db196887955dee19a80df4fb84c7e76eb5095a88e" },
            { "invalid/07-missing-member.code.txt", "3951589b17cad16667dccecc89282077047252b34ba41fea3fc1f19267362951" },
            { "invalid/07-missing-member.input.json", "1fe9425824b846a614de8c11d9d6010d238fccff0b1c450075b50cb1925939b0" },
            { "invalid/08-unsupported-format.code.txt", "2c82a8bad9b4c3698c0a951064675fbf1339936c14029335aaaf31aaf9a26589" },
            { "invalid/08-unsupported-format.input.json", "21726db5e052bd3537d408f6a6b12bcae1481f6cc7edf411d3a67432a51dfbc7" },
            { "invalid/09-float.code.txt", "8d5a5c6786d01ab9c01a382ad6bab90916ae7e518179937ac8e37e1b1674c6b6" },
            { "invalid/09-float.input.json", "c1fdf712816d880bbbf5eb323b624f3657290c2dba5cadea7f23db21e3b290ed" },
            { "invalid/10-exponent.code.txt", "8d5a5c6786d01ab9c01a382ad6bab90916ae7e518179937ac8e37e1b1674c6b6" },
            { "invalid/10-exponent.input.json", "5f662ad4115d7442ef4ccea17e68eb10e093057bc502df1b89c821f717d4d2f0" },
            { "invalid/11-negative-zero.code.txt", "8d5a5c6786d01ab9c01a382ad6bab90916ae7e518179937ac8e37e1b1674c6b6" },
            { "invalid/11-negative-zero.input.json", "55548ee50a28ffcc5c8a9aa5d70a116c2065916a1ac4ef8160c469f7406fe8dc" },
            { "invalid/12-over-bound.code.txt", "cd740d4e75f1383988e9e1ef309ef910794f07bfe4e73653355fd5bba71f9106" },
            { "invalid/12-over-bound.input.json", "da579bda6478e743e96f170f60b7e2868992329f9e058413152ec26d0d019c45" },
            { "invalid/13-uppercase-uuid.code.txt", "ae5e48acf14fa111a2d2b8b1c6dbad1f506554089ac0bad03693de507dac63ea" },
            { "invalid/13-uppercase-uuid.input.json", "7ba5baaf9b6ebac32d0c150f57db7aeea643cbbe38e629c6d9c142cfee56e445" },
            { "invalid/14-non-canonical-base58.code.txt", "ae5e48acf14fa111a2d2b8b1c6dbad1f506554089ac0bad03693de507dac63ea" },
            { "invalid/14-non-canonical-base58.input.json", "5f4f365b428316b842c3deb244a050a181aaffa8ddeddd0ddde3a8ce1b4f1b70" },
            { "invalid/15-non-ascii-objective.code.txt", "787152db8b32fef2ca34dc45b61c10c8980dff2ed0f4c5764613b9dd99c35434" },
            { "invalid/15-non-ascii-objective.input.json", "099bf4662789ae3dc903562d8035a957b47b00903fc24ea860ddb8e77a417ca7" },
            { "invalid/16-bad-enum.code.txt", "bc7b7ea8d8f179bc1a05ce55fd255f58327988df01747fa7643bdc77e4e7050d" },
            { "invalid/16-bad-enum.input.json", "e051a7616242f63d1df78bfcca2a01f993ac9ea042225332dbf2f34957493d1f" },
            { "invalid/17-infra-void.code.txt", "38a587420349a01d913b13470365abc5befeb13518c95ee8f7b636ae354efcf5" },
            { "invalid/17-infra-void.input.json", "22e97fa0039098c2b8c3fd008de23f4997c0205e20fbe348d053f0913527a1f7" },
            { "invalid/18-duplicate-entry.code.txt", "18002309cae5094c3af244eb257a4081b63b7b03c734c743e229cd20a7dc641d" },
            { "invalid/18-duplicate-entry.input.json", "38d16ca6d63b32dc09b6ca6d36d71540838e272490fcbabb636c65e4ed414eb7" },
            { "invalid/19-uuid-not-v7.code.txt", "ae5e48acf14fa111a2d2b8b1c6dbad1f506554089ac0bad03693de507dac63ea" },
            { "invalid/19-uuid-not-v7.input.json", "e094670fcf79c86219089a25fc453e8f6f208234b51c1241acccd024717c7ae3" },
            { "invalid/20-split-stack.code.txt", "18002309cae5094c3af244eb257a4081b63b7b03c734c743e229cd20a7dc641d" },
            { "invalid/20-split-stack.input.json", "9a8466211262b16a3b0612b703220efb4be2ecf796da5b090b1fd71f5f2837f9" },
            { "receipt/01-success.input.json", "e6f720da5ca5f71784e35d181c710015b304a8d0b6fce4774c3f6de04b5a4649" },
            { "receipt/01-success.receipt.json", "de7358c5e1257bfeaa65007b420e7b4488ca4cdedad67bda711130a7aa67c450" },
            { "receipt/02-insured-defeat.input.json", "90bfa5895829086f7075a0cd7dfab4e820eb6b49c748f444d6412d997e889aeb" },
            { "receipt/02-insured-defeat.receipt.json", "31f7491d974933cd14914fbc04b128e648ba195abbe08ae1a7bcc754dd3e0225" },
            { "receipt/03-void.input.json", "164f91a79bc2e0a7f074766cdc9662ec19afff00075d5f03031cc3dd2b1615dc" },
            { "receipt/03-void.receipt.json", "1e422a4345c37ac8a2bd29085d26ae9217d0773df8cd150abb85bfaee3397c31" },
            { "retention/01-priority-then-hash.json", "2a66a194a65b0a4f7515c49ba762001c63448e3f1199409606038840e883db0d" },
            { "retention/02-hash-decides-and-caps-units.json", "91c6b63ea5354115cd3f441c73f77178ac9cc9c29263024f9bd27a9de14d22c8" },
            { "valid/01-minimal.canonical.json", "0c66150575e4fd8efe83d199f4ba056183c3363609ad835d7ef02c94ddddd122" },
            { "valid/01-minimal.hash.txt", "847c8a5c4824438a2a9683e00f9b444d860703bf605948e24a026f8672985995" },
            { "valid/01-minimal.input.json", "274fd468978f10df40a2be6c3e01feca628d1ca430990aa7747686b25e52eb82" },
            { "valid/02-full.canonical.json", "2289eaf580c5427acc364be5ff8578b74cc4ece200479d3e2e6fba7523f1ea01" },
            { "valid/02-full.hash.txt", "2860a163a7a0289b2073206bc426c0168c7c911c2dd258a01f600b8786ccf6f6" },
            { "valid/02-full.input.json", "5e348b9a779922456d9f6f35c9bc463f1be83ad8266797bce88e9dcf9b5dd254" },
            { "valid/03-success.canonical.json", "796dad15ff53406eaa7043b8e3735aa5b3a1102351aa2cec823217d8680fc204" },
            { "valid/03-success.hash.txt", "444c31d48d25a44c51930b00de89ccb55f1d59cf2e0e1e72547886c321409bba" },
            { "valid/03-success.input.json", "1dc6a3b6e9a16c1dfbc09ae69339a2219cddc0b1aeacd10760512ab8874c0dfe" },
            { "valid/04-defeat.canonical.json", "be0992bfea3a473691d3deffa456ac16593ddf0181d01967f0c97ecc1010e1ad" },
            { "valid/04-defeat.hash.txt", "38e0789f7f6a1766021748a8a892891ed5dd553dc440f164906c99f600fb0f34" },
            { "valid/04-defeat.input.json", "45b401068ce7a0aed8d802e7bf0441c60d9cf22e891e530e95b8562925c330d9" },
            { "valid/05-abandoned.canonical.json", "4ea782ec3624634e986b8e47038fb098244cb336bbbd46a3b5d8737dba4c91a1" },
            { "valid/05-abandoned.hash.txt", "d903b5fa4c3ee29dc7ece51c2924489ea7473f082dac5b01229d172efde2699d" },
            { "valid/05-abandoned.input.json", "f220a0b57c6414d196687158d847162dfa80b40953cfb8ca0ed90272e0b44435" },
            { "valid/06-disconnected.canonical.json", "b44a16682e68eefe4d0640e59ed84fe13b852954e8607ca859062879028d99b5" },
            { "valid/06-disconnected.hash.txt", "f53d5685935e684ce8b32c4ddb37f90b86809770c3e50af24b0bc7771c945d8c" },
            { "valid/06-disconnected.input.json", "5a9ea777b899e2d68b0e60d60016c0559392653e7384b476ec1a7950dc12dca0" },
            { "valid/07-expired.canonical.json", "6d204ddc12544f548982568e5781ae22a2d7697a4cd5665d81b99476df3ffa90" },
            { "valid/07-expired.hash.txt", "433137a703828245bb58f227be55e6445080e7d3d49eec760ea36e8464be7aeb" },
            { "valid/07-expired.input.json", "7583c09eb2cb8e42a2ddcafff625b9ec916f41d251531b33d332f36e137709cf" },
            { "valid/08-escapes.canonical.json", "0c66150575e4fd8efe83d199f4ba056183c3363609ad835d7ef02c94ddddd122" },
            { "valid/08-escapes.hash.txt", "847c8a5c4824438a2a9683e00f9b444d860703bf605948e24a026f8672985995" },
            { "valid/08-escapes.input.json", "44f3741d940d947553f68ccc6c8fa79d045881e37aee10d2b5afe0391c7e3514" },
            { "valid/09-max-bounds.canonical.json", "0ec8116e37ad5bc3083cb54bd01c7da7ed7dff3655f6e17c767de3123deeb3fb" },
            { "valid/09-max-bounds.hash.txt", "2903b34706e922875346a9e811e207b351ca9e01a96ff5c09f85a2e5401d3bd9" },
            { "valid/09-max-bounds.input.json", "58f5928550e664d01024f1cd0dc981f1096a05865fc4c31e8e101a6c11b7f64c" },
            { "valid/10-reverse-lists.canonical.json", "2289eaf580c5427acc364be5ff8578b74cc4ece200479d3e2e6fba7523f1ea01" },
            { "valid/10-reverse-lists.hash.txt", "2860a163a7a0289b2073206bc426c0168c7c911c2dd258a01f600b8786ccf6f6" },
            { "valid/10-reverse-lists.input.json", "cfc53217e4eb008e8d5b7d35863dfb740b773ca30bee46b64ca5d24ab12b069a" },
            { "valid/11-whitespace.canonical.json", "2289eaf580c5427acc364be5ff8578b74cc4ece200479d3e2e6fba7523f1ea01" },
            { "valid/11-whitespace.hash.txt", "2860a163a7a0289b2073206bc426c0168c7c911c2dd258a01f600b8786ccf6f6" },
            { "valid/11-whitespace.input.json", "04acbcca477448e88cc2a4434014ff93572cd9d5033b33f97024f7d6582a5acb" },
            { "valid/12-key-order.canonical.json", "2289eaf580c5427acc364be5ff8578b74cc4ece200479d3e2e6fba7523f1ea01" },
            { "valid/12-key-order.hash.txt", "2860a163a7a0289b2073206bc426c0168c7c911c2dd258a01f600b8786ccf6f6" },
            { "valid/12-key-order.input.json", "2bb29127f60288b280bb38a39495b09320b964a055def0290da4e48eaf962a7d" },
            { "valid/13-ledger-mismatch.canonical.json", "e25eb592f730da441ba3dea21a6f89f3b87e24b8daabf254760dc2a36141e5ad" },
            { "valid/13-ledger-mismatch.hash.txt", "005b6d2f4215947b4e84ec4f5f6f614aa26fcce2c77ac0a4385866207c6e1505" },
            { "valid/13-ledger-mismatch.input.json", "98530409ad8e7a28a3f8303cde5d0e75ba59d0dce3ffbd618f187c5e4ce12cba" },
        };
    }
}
