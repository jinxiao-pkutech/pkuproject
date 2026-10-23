using ScreenFlow.Core.Ir;
using ScreenFlow.Core.Storage;
using ScreenFlow.Core.Tests.ConfigTests;
using ScreenFlow.Core.Tests.IrTests;
using Xunit;

namespace ScreenFlow.Core.Tests.StorageTests;

/// <summary>The index database (02 §7): writing, reading, collision handling and determinism.</summary>
public sealed class StorageTests
{
    [Fact]
    [Trait("Task", "T03")]
    public void Storage_WritesAndReadsAllTables()
    {
        using var db = IndexDb.CreateInMemory();

        using (var writer = new IrWriter(db))
        {
            foreach (var record in IrSamples.AllRecords.Values) writer.Write(record);
            writer.Flush();
        }

        var reader = new IndexReader(db);

        var symbol = reader.Symbol(IrSamples.MethodId);
        Assert.NotNull(symbol);
        Assert.Equal("Fixture.Sample.Run(System.String)", symbol.FqName);
        Assert.Equal(SymbolKinds.Method, symbol.Kind);
        // The whole record comes back out of the json column, including fields no column holds.
        Assert.Single(symbol.Attributes);
        Assert.Equal("System.ObsoleteAttribute", symbol.Attributes[0].TypeFqName);

        Assert.Single(reader.SymbolsByName("Run"));
        Assert.Single(reader.SymbolsInContainer(IrSamples.TypeId));
        Assert.Single(reader.SymbolsInFile(IrSamples.File));

        var callsite = reader.Callsite(IrSamples.CallsiteId);
        Assert.NotNull(callsite);
        Assert.True(callsite.TargetIsVirtual);
        Assert.True(callsite.IsAwaited);
        Assert.Single(callsite.Conditions);
        Assert.Equal(StringTestOps.EqualsOp, callsite.Conditions[0].Tests[0].Op);
        Assert.Single(reader.CallsitesIn(IrSamples.MethodId));
        Assert.Single(reader.CallsitesInRange(IrSamples.File, 1, 100));

        var assign = reader.Assign(IrSamples.AssignId);
        Assert.NotNull(assign);
        Assert.Equal(AssignRec.Operators.AddAssign, assign.Operator);
        // An event subscription's target resolves to the member it points at (02 §7.2).
        Assert.Single(reader.AssignsTargeting(IrSamples.FieldId));

        Assert.Single(reader.CreationsIn(IrSamples.MethodId));
        Assert.Single(reader.CreationsOfType(IrSamples.TypeId));

        var cfg = reader.Cfg(IrSamples.MethodId);
        Assert.NotNull(cfg);
        Assert.True(cfg.HasGoto);
        Assert.Equal(12, cfg.BlockCount);

        var file = reader.File(IrSamples.File);
        Assert.NotNull(file);
        Assert.Equal(Encodings.Cp932, file.Encoding);

        var markers = reader.AllMarkers();
        Assert.Single(markers);
        Assert.Equal(MarkerDirections.Begin, markers[0].Dir);
        Assert.Equal(new[] { "SEJ-1234" }, markers[0].IssueRefs.ToArray());

        Assert.Single(reader.ImplementationsOf(IrSamples.SymbolId("M:Fixture.IRunner.Run")));
        Assert.Single(reader.Diagnostics());
        Assert.Equal("P4", reader.Diagnostics()[0].Phase);

        // Evidence is derived from every record carrying a span (T03 §3): the symbol, the callsite,
        // the assignment and the creation all sit at the same span here, so they collapse onto one.
        Assert.Equal(1, db.Count("evidence"));
        var evidence = reader.Evidence(Ids.EvidenceId(IrSamples.File, Span.Of(10, 5, 10, 40)));
        Assert.NotNull(evidence);
        Assert.Equal(IrSamples.File, evidence.File);
        Assert.Null(evidence.SnippetHash);
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_WritesAndReadsDomainTables()
    {
        using var db = IndexDb.CreateInMemory();

        using (var writer = new DomainWriter(db))
        {
            writer.Add(new ScreenRow
            {
                Terminal = "customer", ScreenId = "SSAW0903", Name = "Edy charge",
                IsPopup = false, ViewTypeId = IrSamples.TypeId, Registered = true
            });
            writer.Add(new ScreenRow
            {
                Terminal = "employee", ScreenId = "SSAW9999", Registered = false
            });
            writer.Add(new ControlRow
            {
                Terminal = "customer", ScreenId = "SSAW0903", ControlId = "BtnOther",
                ControlType = "Button", TextKey = "SSAW0903_12", TextKeySource = "initJson",
                TextKeyConfidence = "certain"
            });
            writer.Add(new ControlHandlerRow
            {
                Terminal = "customer", ScreenId = "SSAW0903", ControlId = "BtnOther",
                EventName = "clicked", HandlerMethodId = IrSamples.MethodId, Confidence = "certain"
            });
            writer.Add(new TextRow
            {
                Collection = "StringLocalizationTable_Customer", Key = "SSAW0903_12", Locale = "ja",
                TextRaw = "その他の金額を入力<br>(1,000円単位)",
                TextPlain = "その他の金額を入力(1,000円単位)"
            });
            writer.Add(new MessageRow { Code = 22, Locale = "ja", TextPlain = "金額が上限を超えています" });
            writer.Add(new ScreenEdgeRow
            {
                Id = "se_0123456789abcdef0123", FromTerminal = "customer", FromScreen = "SSAW0903",
                ToTerminal = "customer", ToScreen = "SSAW0907", Trigger = "click",
                ControlId = "BtnOther", Confidence = "candidate", Cost = 4.25, Json = "{}"
            });
            writer.Add(new BreakRow
            {
                Id = "b_0123456789abcdef0123", Category = "RemoteDisplay",
                Reason = "RuntimeStateDependentDispatch", ResumeConfidence = "multiple", Json = "{}"
            });
            writer.Add(new DomainEdgeRow
            {
                Id = "de_0123456789abcdef012", Kind = "messageSend",
                FromMethodId = IrSamples.MethodId, Json = """{"keys":["Common.Display"]}"""
            });
            writer.Flush();
        }

        var reader = new IndexReader(db);

        var screens = reader.Screens();
        Assert.Equal(2, screens.Count);
        // An unregistered screen is kept, not dropped: it is a known source of break links.
        Assert.Contains(screens, s => s is { ScreenId: "SSAW9999", Registered: false });

        var customer = reader.Screens("SSAW0903", "customer");
        Assert.Single(customer);
        Assert.Equal("Edy charge", customer[0].Name);
        Assert.False(customer[0].IsPopup);

        var controls = reader.Controls("customer", "SSAW0903");
        Assert.Single(controls);
        Assert.Equal("SSAW0903_12", controls[0].TextKey);

        Assert.Single(reader.ControlHandlers("customer", "SSAW0903"));

        var text = reader.Text("StringLocalizationTable_Customer", "SSAW0903_12", "ja");
        Assert.NotNull(text);
        Assert.Contains("<br>", text.TextRaw!, StringComparison.Ordinal);
        Assert.DoesNotContain("<br>", text.TextPlain!, StringComparison.Ordinal);

        Assert.NotNull(reader.Message(22, "ja"));
        Assert.Single(reader.ScreenEdgesFrom("customer", "SSAW0903"));
        Assert.Single(reader.ScreenEdgesTo("customer", "SSAW0907"));
        Assert.Single(reader.Breaks("RemoteDisplay"));
        Assert.Single(reader.DomainEdges("messageSend"));
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_IdCollision_Throws()
    {
        using var db = IndexDb.CreateInMemory();
        using var writer = new IrWriter(db);

        var original = (SymbolRec)IrSamples.AllRecords[IrRecordKinds.Symbol];
        writer.Write(original);
        writer.Flush();

        // Same id, same content: a re-emitted record is simply ignored (T03 §2).
        writer.Write(original);
        writer.Flush();
        Assert.Equal(1, db.Count("symbols"));

        // Same id, different content: fatal, because merging them would conflate two symbols.
        var conflicting = original with { FqName = "Fixture.Sample.RunDifferently(System.String)" };
        writer.Write(conflicting);

        var error = Assert.Throws<IdCollisionException>(() => writer.Flush());
        Assert.Equal("symbols", error.Table);
        Assert.Equal(original.Id, error.Id);
        Assert.Contains(IdCollisionException.ErrorCode, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_Dump_IsOrderIndependent()
    {
        var forward = IrSamples.AllRecords.Values.ToList();
        var reversed = Enumerable.Reverse(forward).ToList();

        var firstHash = DumpOf(forward);
        var secondHash = DumpOf(reversed);

        Assert.Equal(firstHash, secondHash);
        Assert.Equal(64, firstHash.Length);

        // A real difference must still change the hash, or the test would prove nothing.
        var changed = forward.Select(r => r is FileRec file ? file with { LineCount = 43 } : r).ToList();
        Assert.NotEqual(firstHash, DumpOf(changed));

        static string DumpOf(IReadOnlyList<IrRecord> records)
        {
            using var db = IndexDb.CreateInMemory();
            using (var writer = new IrWriter(db))
            {
                foreach (var record in records) writer.Write(record);
                writer.Flush();
            }

            // builtAt is excluded from the export, so two builds at different times still agree.
            db.SetMeta(MetaKeys.BuiltAt, DateTimeOffset.UtcNow.ToString("O"));
            db.SetMeta(MetaKeys.IrVersion, IrValidator.SupportedIrVersion);
            return ScreenFlowDump.Hash(db);
        }
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_Dump_ExcludesBuiltAtOnly()
    {
        using var db = IndexDb.CreateInMemory();
        db.SetMeta(MetaKeys.IrVersion, "1");
        db.SetMeta(MetaKeys.ProfileId, "fixture");
        db.SetMeta(MetaKeys.BuiltAt, "2026-01-01T00:00:00Z");

        using var stream = new MemoryStream();
        var result = ScreenFlowDump.Write(db, stream);
        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.Equal(2, result.Lines);
        Assert.Contains("\"irVersion\"", text, StringComparison.Ordinal);
        Assert.Contains("\"profileId\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(MetaKeys.BuiltAt, text, StringComparison.Ordinal);

        // Line format is table, tab, JSON (02 §7.5).
        Assert.All(
            text.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.Contains('\t', line));
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_AtomicBuild_KeepsOldDbOnFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "screenflow-atomic-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "fixture.db");

        try
        {
            // A first successful build establishes the index.
            using (var build = AtomicBuild.Begin(target))
            {
                build.Db.SetMeta(MetaKeys.ProfileId, "first-build");
                build.Commit();
            }

            Assert.True(File.Exists(target));
            using (var opened = IndexDb.Open(target))
                Assert.Equal("first-build", opened.GetMeta(MetaKeys.ProfileId));

            // A second build that throws must leave the first one in place. The delegate type is
            // pinned because a lambda whose every path throws is convertible to any delegate, which
            // makes the Assert.Throws overload resolution pick the async one.
            Action failingBuild = () =>
            {
                using var build = AtomicBuild.Begin(target);
                build.Db.SetMeta(MetaKeys.ProfileId, "second-build");
                throw new InvalidOperationException("extraction failed");
            };
            Assert.Throws<InvalidOperationException>(failingBuild);

            Assert.True(File.Exists(target));
            Assert.False(File.Exists(target + AtomicBuild.BuildingSuffix));
            using (var opened = IndexDb.Open(target))
                Assert.Equal("first-build", opened.GetMeta(MetaKeys.ProfileId));

            // A third, successful build replaces it.
            using (var build = AtomicBuild.Begin(target))
            {
                build.Db.SetMeta(MetaKeys.ProfileId, "third-build");
                build.Commit();
            }

            using (var opened = IndexDb.Open(target))
                Assert.Equal("third-build", opened.GetMeta(MetaKeys.ProfileId));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_Fingerprint_DetectsChange()
    {
        var directory = Path.Combine(Path.GetTempPath(), "screenflow-fp-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        try
        {
            var source = Path.Combine(directory, "Sample.cs");
            File.WriteAllText(source, "class Sample { }");

            var baseline = Fingerprint.Compute(new[] { source }, RepoLayout.ConfigDirectory);
            Assert.True(Fingerprint.Matches(baseline.ToString(), baseline));
            Assert.Empty(Fingerprint.Differences(baseline, baseline));

            // Round-trips through the stored string form.
            var parsed = Fingerprint.Value.TryParse(baseline.ToString());
            Assert.Equal(baseline, parsed);

            // A longer file changes the total size.
            File.WriteAllText(source, "class Sample { int field; }");
            File.SetLastWriteTimeUtc(source, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var afterEdit = Fingerprint.Compute(new[] { source }, RepoLayout.ConfigDirectory);
            Assert.False(Fingerprint.Matches(baseline, afterEdit));
            Assert.Contains(Fingerprint.Differences(baseline, afterEdit), d => d.Contains("total size", StringComparison.Ordinal));

            // An added file changes the count.
            var second = Path.Combine(directory, "Other.cs");
            File.WriteAllText(second, "class Other { }");
            var afterAdd = Fingerprint.Compute(new[] { source, second }, RepoLayout.ConfigDirectory);
            Assert.False(Fingerprint.Matches(afterEdit, afterAdd));
            Assert.Contains(Fingerprint.Differences(afterEdit, afterAdd), d => d.Contains("file count", StringComparison.Ordinal));

            // A different configuration changes the hash even when the sources do not.
            var otherConfig = Path.Combine(directory, "config");
            Directory.CreateDirectory(otherConfig);
            File.WriteAllText(Path.Combine(otherConfig, "profiles.json"), "{}");
            var afterConfig = Fingerprint.Compute(new[] { source, second }, otherConfig);
            Assert.False(Fingerprint.Matches(afterAdd, afterConfig));
            Assert.Contains(Fingerprint.Differences(afterAdd, afterConfig), d => d.Contains("configuration", StringComparison.Ordinal));

            // No stored fingerprint at all is reported rather than treated as a match.
            Assert.False(Fingerprint.Matches((Fingerprint.Value?)null, afterConfig));
            Assert.Contains(Fingerprint.Differences(null, afterConfig), d => d.Contains("no fingerprint", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_SchemaDeclaresEveryTableOf02Section7()
    {
        var expected = new[]
        {
            // 02 §7.2
            "meta", "files", "symbols", "impls", "callsites", "assigns", "creations", "cfgs",
            "markers", "evidence", "diagnostics",
            // 02 §7.3
            "screens", "enum_members", "controls", "control_handlers", "texts", "messages",
            "input_constraints", "keyseq_transitions", "di_registrations", "domain_edges",
            "message_handlers", "entries", "boundaries", "breaks", "screen_edges"
        };

        Assert.Equal(
            expected.OrderBy(t => t, StringComparer.Ordinal).ToArray(),
            IndexDb.SchemaTables.OrderBy(t => t, StringComparer.Ordinal).ToArray());

        using var db = IndexDb.CreateInMemory();
        foreach (var table in expected) Assert.Equal(0, db.Count(table));
        Assert.Equal(expected.Length, db.AllCounts().Count);
    }

    [Fact]
    [Trait("Task", "T03")]
    public void Storage_BuildModeSwitchesToWalOnSeal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "screenflow-mode-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fixture.db");

        try
        {
            using (var db = IndexDb.Create(path, DbMode.Build))
            {
                Assert.Equal(DbMode.Build, db.Mode);
                db.SealForQuerying();
                Assert.Equal(DbMode.Query, db.Mode);
            }

            using var reopened = IndexDb.Open(path);
            Assert.Equal(DbMode.Query, reopened.Mode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
