using System;
using System.Collections.Generic;

namespace MusicBox.Services
{
    public static class LocalizationService
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Resources = new(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-Hans"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["window.title"] = "\u97f3\u4e50\u9b54\u76d2",
                ["nav.editor"] = "\u4e94\u7ebf\u8c31",
                ["nav.convert"] = "\u8f6c\u6362",
                ["nav.compose"] = "\u521b\u4f5c",
                ["nav.recognize"] = "\u8bc6\u522b",
                ["nav.settings"] = "\u8bbe\u7f6e",
                ["compose.page_title"] = "\u667a\u80fd\u521b\u4f5c",
                ["compose.page_subtitle"] = "\u7cfb\u7edf\u4f1a\u81ea\u52a8\u51b3\u5b9a\u901f\u5ea6\u3001\u62cd\u53f7\u548c\u8c03\u6027\uff0c\u4e00\u6b21\u7ed9\u51fa 3 \u4e2a\u660e\u663e\u4e0d\u540c\u7684\u5019\u9009\u65b9\u6848\u3002",
                ["compose.label.title"] = "\u6807\u9898",
                ["compose.label.mood"] = "\u60c5\u7eea",
                ["compose.label.length"] = "\u957f\u5ea6",
                ["compose.default_title"] = "\u667a\u80fd\u521b\u4f5c",
                ["compose.mood.calm"] = "\u5e73\u9759",
                ["compose.mood.positive"] = "\u79ef\u6781",
                ["compose.mood.sad"] = "\u4f24\u611f",
                ["compose.mood.sleep"] = "\u52a9\u7720",
                ["compose.mood.hopeful"] = "\u5e0c\u671b",
                ["compose.mood.nostalgic"] = "\u6000\u65e7",
                ["compose.mood.dreamy"] = "\u68a6\u5e7b",
                ["compose.mood.tense"] = "\u7d27\u5f20",
                ["compose.length.short"] = "\u77ed",
                ["compose.length.medium"] = "\u4e2d",
                ["compose.length.long"] = "\u957f",
                ["compose.option.a"] = "\u65b9\u6848 A",
                ["compose.option.b"] = "\u65b9\u6848 B",
                ["compose.option.c"] = "\u65b9\u6848 C",
                ["compose.action.generate"] = "\u751f\u6210",
                ["compose.action.retry"] = "\u91cd\u8bd5",
                ["compose.action.apply_to_editor"] = "\u5199\u5165\u4e94\u7ebf\u8c31",
                ["compose.action.waiting"] = "\u7b49\u5f85\u751f\u6210",
                ["compose.action.play"] = "\u64ad\u653e",
                ["compose.action.pause"] = "\u6682\u505c",
                ["compose.action.save_audio"] = "\u4fdd\u5b58\u97f3\u9891",
                ["compose.action.keep"] = "\u4fdd\u7559\u65b9\u6848",
                ["compose.action.unkeep"] = "\u53d6\u6d88\u4fdd\u7559",
                ["compose.action.save_rating"] = "\u4fdd\u5b58\u8bc4\u5206",
                ["compose.action.rate_details"] = "\u5206\u7c7b\u8bc4\u5206",
                ["compose.meta.key"] = "\u8c03\u53f7",
                ["compose.meta.meter"] = "\u62cd\u53f7",
                ["compose.meta.measures"] = "\u5c0f\u8282\u6570",
                ["compose.meta.tempo"] = "\u901f\u5ea6",
                ["compose.meta.duration"] = "\u603b\u65f6\u957f",
                ["compose.meta.preference_fit"] = "\u504f\u597d\u9884\u6d4b",
                ["compose.meta.user_rating"] = "\u4f60\u7684\u8bc4\u5206",
                ["compose.meta.predicted_breakdown"] = "\u9884\u6d4b\u5206\u7c7b",
                ["compose.meta.model_kind"] = "\u6392\u5e8f\u6a21\u578b",
                ["compose.meta.creation_reason"] = "\u521b\u4f5c\u539f\u56e0",
                ["compose.meta.ranking_reason"] = "\u6392\u5e8f\u539f\u56e0",
                ["compose.mode.major"] = "\u5927\u8c03",
                ["compose.mode.minor"] = "\u5c0f\u8c03",
                ["compose.status.ready"] = "\u667a\u80fd\u521b\u4f5c\u9875\u9762\u5df2\u6253\u5f00\u3002",
                ["compose.status.generated"] = "\u5df2\u751f\u6210 {0} \u7ec4\u5019\u9009\u65b9\u6848\u3002",
                ["compose.status.reranked"] = "\u5df2\u751f\u6210 {0} \u7ec4\u5019\u9009\u65b9\u6848\uff0c\u5e76\u6309\u504f\u597d\u6392\u5e8f\uff08\u57fa\u4e8e {1} \u6761\u8bc4\u5206\uff09\u3002",
                ["compose.status.applied"] = "\u5df2\u91c7\u7528\u65b9\u6848 {0} \u5e76\u5199\u5165\u4e94\u7ebf\u8c31\uff1a{1}",
                ["compose.status.rating_saved"] = "\u5df2\u8bb0\u5f55\u65b9\u6848 {0} \u7684\u8bc4\u5206\uff1a{1}\uff08\u7d2f\u8ba1 {2} \u6761\uff09",
                ["compose.status.rating_cleared"] = "\u5df2\u6e05\u9664\u65b9\u6848 {0} \u7684\u8bc4\u5206\uff08\u5269\u4f59 {1} \u6761\uff09",
                ["compose.status.audio_exported"] = "\u65b9\u6848 {0} \u5df2\u4fdd\u5b58\u4e3a WAV\uff1a{1}",
                ["compose.status.audio_export_failed"] = "\u65b9\u6848 {0} \u4fdd\u5b58\u97f3\u9891\u5931\u8d25\uff1a{1}",
                ["compose.status.generation_failed"] = "\u751f\u6210\u5931\u8d25\uff1a{0}",
                ["compose.status.failed_short"] = "\u751f\u6210\u5931\u8d25",
                ["compose.rate.melody"] = "\u65cb\u5f8b",
                ["compose.rate.rhythm"] = "\u8282\u594f",
                ["compose.rate.harmony"] = "\u548c\u58f0",
                ["compose.rate.mood_fit"] = "\u60c5\u7eea\u543b\u5408",
                ["compose.rate.overall"] = "\u6574\u4f53",
                ["compose.rate.dialog_title"] = "\u4e3a\u65b9\u6848 {0} \u5206\u7c7b\u8bc4\u5206",
                ["compose.rate.dialog_hint"] = "\u53ef\u4ee5\u5355\u72ec\u7ed9\u65cb\u5f8b\u3001\u8282\u594f\u3001\u548c\u58f0\u3001\u60c5\u7eea\u543b\u5408\u548c\u6574\u4f53\u6253\u5206\u3002\u518d\u6b21\u4fdd\u5b58\u4f1a\u8986\u76d6\u65e7\u8bc4\u5206\u3002",
                ["compose.rate.dialog_save"] = "\u4fdd\u5b58",
                ["compose.rate.dialog_clear"] = "\u6e05\u9664\u8bc4\u5206",
                ["compose.rate.dialog_cancel"] = "\u53d6\u6d88",
                ["compose.model.trained"] = "\u672c\u5730\u8bad\u7ec3\u6392\u5e8f\u6a21\u578b",
                ["compose.model.heuristic"] = "\u542f\u53d1\u5f0f\u9884\u4f30",
                ["compose.reason.separator"] = "\uff0c",
                ["compose.reason.creation_template"] = "\u4e3a\u4e86\u66f4\u8d34\u8fd1\u201c{0}\u201d\u7684\u611f\u89c9\uff0c\u8fd9\u9996\u66f4\u503e\u5411\u4e8e{1}\u3002",
                ["compose.reason.ranking_template"] = "{0}\uff1a{1}\uff0c{2}\u8fd9\u4e00\u7c7b\u66f4\u7a81\u51fa\uff0c\u800c\u4e14{3}\u3002",
                ["compose.reason.smooth_melody"] = "\u65cb\u5f8b\u8d77\u4f0f\u66f4\u5e73\u7a33",
                ["compose.reason.bold_melody"] = "\u65cb\u5f8b\u8df3\u8fdb\u66f4\u660e\u663e",
                ["compose.reason_spacious_rhythm"] = "\u8282\u594f\u7559\u767d\u66f4\u591a",
                ["compose.reason_dense_rhythm"] = "\u8282\u594f\u66f4\u7d27\u5bc6",
                ["compose.reason_rich_harmony"] = "\u548c\u58f0\u66f4\u9971\u6ee1",
                ["compose.reason_light_harmony"] = "\u548c\u58f0\u66f4\u8f7b",
                ["compose.reason.top_rank"] = "\u6574\u4f53\u9884\u6d4b\u5206\u5728\u672c\u8f6e\u6700\u9ad8",
                ["compose.reason.above_average_rank"] = "\u6574\u4f53\u9884\u6d4b\u5206\u9ad8\u4e8e\u672c\u8f6e\u5e73\u5747",
                ["compose.reason.balanced_profile"] = "\u6574\u4f53\u5747\u8861\u6027",
                ["compose.reason.more_mood_aligned"] = "\u66f4\u8d34\u5408\u5f53\u524d\u60c5\u7eea",
                ["compose.reason.more_coordinated"] = "\u540c\u65f6\u97f3\u7684\u65f6\u503c\u66f4\u534f\u8c03",
                ["compose.reason.more_stable"] = "\u65cb\u5f8b\u5927\u8df3\u66f4\u5c11",
                ["compose.reason.more_aligned"] = "\u66f4\u63a5\u8fd1\u4f60\u8fc7\u5f80\u7684\u9ad8\u5206\u6837\u672c",
                ["compose.reason.trained_model"] = "\u8bad\u7ec3\u6a21\u578b\u5224\u65ad",
                ["compose.reason.heuristic_model"] = "\u542f\u53d1\u5f0f\u5224\u65ad",
                ["settings.page_title"] = "\u8bbe\u7f6e",
                ["settings.section.personalization"] = "\u4e2a\u6027\u5316",
                ["settings.theme"] = "\u4e3b\u9898",
                ["settings.theme.system"] = "\u8ddf\u968f\u7cfb\u7edf",
                ["settings.theme.light"] = "\u6d45\u8272",
                ["settings.theme.dark"] = "\u6df1\u8272",
                ["settings.language"] = "\u754c\u9762\u8bed\u8a00",
                ["settings.language.system"] = "\u8ddf\u968f\u7cfb\u7edf",
                ["settings.language.zh"] = "\u7b80\u4f53\u4e2d\u6587",
                ["settings.language.en"] = "English",
                ["settings.section.about"] = "\u8f6f\u4ef6\u4fe1\u606f",
                ["settings.about.name"] = "\u97f3\u4e50\u9b54\u76d2",
                ["settings.about.version"] = "\u7248\u672c\u53f7",
                ["settings.about.build"] = "\u6784\u5efa\u53f7",
                ["settings.about.author"] = "\u4f5c\u8005",
                ["settings.restart.title"] = "\u91cd\u542f\u63d0\u793a",
                ["settings.restart.content"] = "\u4fee\u6539\u754c\u9762\u8bed\u8a00\u9700\u8981\u91cd\u542f\u5e94\u7528\uff0c\u672a\u4fdd\u5b58\u5185\u5bb9\u53ef\u80fd\u4e22\u5931\u3002\u662f\u5426\u73b0\u5728\u91cd\u542f\uff1f",
                ["settings.restart.confirm"] = "\u7acb\u5373\u91cd\u542f",
                ["settings.restart.cancel"] = "\u7a0d\u540e",
                ["editor.menu.file"] = "\u6587\u4ef6",
                ["editor.menu.new"] = "\u65b0\u5efa",
                ["editor.menu.open"] = "\u6253\u5f00",
                ["editor.menu.save"] = "\u4fdd\u5b58",
                ["editor.menu.save_as"] = "\u53e6\u5b58\u4e3a",
                ["editor.menu.import_musicxml"] = "\u5bfc\u5165 MusicXML",
                ["editor.menu.export_musicxml"] = "\u5bfc\u51fa MusicXML",
                ["editor.menu.export_audio"] = "\u5bfc\u51fa WAV",
                ["editor.menu.export_pdf"] = "\u5bfc\u51fa PDF",
                ["editor.menu.print"] = "\u6253\u5370...",
                ["editor.menu.time_signature"] = "\u62cd\u53f7",
                ["editor.menu.key_signature"] = "\u8c03\u53f7",
                ["editor.menu.tempo"] = "\u901f\u5ea6",
                ["editor.menu.note_snap"] = "\u97f3\u7b26\u5438\u9644",
                ["editor.menu.display"] = "\u663e\u793a",
                ["editor.menu.grid"] = "\u7f51\u683c",
                ["editor.menu.area_select"] = "\u533a\u57df\u9009\u62e9",
                ["editor.menu.clear"] = "\u6e05\u7a7a",
                ["editor.new_confirm.title"] = "\u65b0\u5efa\u4e50\u8c31\uff1f",
                ["editor.new_confirm.content"] = "\u5f53\u524d\u4e50\u8c31\u5df2\u6709\u5185\u5bb9\uff0c\u65b0\u5efa\u4f1a\u6e05\u7a7a\u5f53\u524d\u5185\u5bb9\u3002\u662f\u5426\u7ee7\u7eed\uff1f",
                ["editor.new_confirm.primary"] = "\u7ee7\u7eed\u65b0\u5efa",
                ["editor.new_confirm.cancel"] = "\u53d6\u6d88",
                ["editor.toolbar.undo"] = "\u64a4\u9500 (Ctrl+Z)",
                ["editor.toolbar.redo"] = "\u6062\u590d (Ctrl+Y)",
                ["editor.toolbar.expressions"] = "\u8868\u60c5\u8bb0\u53f7",
                ["editor.toolbar.pedal"] = "\u8e0f\u677f",
                ["editor.toolbar.slur"] = "\u8fde\u97f3",
                ["editor.toolbar.duration"] = "\u65f6\u503c",
                ["editor.toolbar.note_type"] = "\u97f3\u7b26\u7c7b\u578b",
                ["editor.toolbar.add_system"] = "\u52a0\u4e00\u884c",
                ["editor.toolbar.add_system_tooltip"] = "\u589e\u52a0\u4e00\u884c\u4e94\u7ebf\u8c31",
                ["editor.toolbar.play"] = "\u64ad\u653e",
                ["editor.toolbar.pause"] = "\u6682\u505c",
                ["editor.toolbar.stop"] = "\u505c\u6b62",
                ["editor.note_length.none"] = "\u65e0",
                ["editor.note_length.whole"] = "\u5168\u97f3\u7b26",
                ["editor.note_length.half"] = "\u4e8c\u5206",
                ["editor.note_length.quarter"] = "\u56db\u5206",
                ["editor.note_length.eighth"] = "\u516b\u5206",
                ["editor.note_length.sixteenth"] = "\u5341\u516d\u5206",
                ["editor.note_length.thirty_second"] = "\u4e09\u5341\u4e8c\u5206",
                ["editor.duration_mode.note"] = "\u97f3\u7b26",
                ["editor.duration_mode.rest"] = "\u4f11\u6b62\u7b26",
                ["editor.note_type.sharp"] = "\u5347\u53f7",
                ["editor.note_type.double_sharp"] = "\u91cd\u5347\u53f7",
                ["editor.note_type.flat"] = "\u964d\u53f7",
                ["editor.note_type.double_flat"] = "\u91cd\u964d\u53f7",
                ["editor.note_type.natural"] = "\u8fd8\u539f\u53f7",
                ["editor.note_type.staccato"] = "\u8df3\u97f3",
                ["editor.note_type.staccatissimo"] = "\u987f\u97f3",
                ["editor.note_type.accent"] = "\u91cd\u97f3",
                ["editor.note_type.dot"] = "\u9644\u70b9",
                ["editor.expression.cresc_symbol"] = "\u6e10\u54cd\u7b26\u53f7",
                ["editor.expression.dim_symbol"] = "\u6e10\u8f7b\u7b26\u53f7",
                ["editor.expression.ottava"] = "\u516b\u5ea6\u8bb0\u53f7",
                ["editor.expression.pedal"] = "\u8e0f\u677f",
                ["editor.expression.pedal_release"] = "\u8e0f\u677f\u62ac\u8d77",
                ["editor.expression.pedal_line"] = "\u5207\u5206\u8e0f\u677f\u7ebf",
                ["editor.expression.tune"] = "\u8c03\u97f3\u8bb0\u53f7",
                ["editor.expression.stacc"] = "\u987f\u97f3\u8bb0\u53f7"
            },
            ["en-US"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["window.title"] = "MusicBox",
                ["nav.editor"] = "Staff",
                ["nav.convert"] = "Convert",
                ["nav.compose"] = "Create",
                ["nav.recognize"] = "Detect",
                ["nav.settings"] = "Prefs",
                ["compose.page_title"] = "Smart Compose",
                ["compose.page_subtitle"] = "The system decides tempo, meter, and key automatically, then returns three clearly different ideas.",
                ["compose.label.title"] = "Title",
                ["compose.label.mood"] = "Mood",
                ["compose.label.length"] = "Length",
                ["compose.default_title"] = "Smart Compose",
                ["compose.mood.calm"] = "Calm",
                ["compose.mood.positive"] = "Positive",
                ["compose.mood.sad"] = "Sad",
                ["compose.mood.sleep"] = "Sleep",
                ["compose.mood.hopeful"] = "Hopeful",
                ["compose.mood.nostalgic"] = "Nostalgic",
                ["compose.mood.dreamy"] = "Dreamy",
                ["compose.mood.tense"] = "Tense",
                ["compose.length.short"] = "Short",
                ["compose.length.medium"] = "Medium",
                ["compose.length.long"] = "Long",
                ["compose.option.a"] = "Option A",
                ["compose.option.b"] = "Option B",
                ["compose.option.c"] = "Option C",
                ["compose.action.generate"] = "Generate",
                ["compose.action.retry"] = "Retry",
                ["compose.action.apply_to_editor"] = "Write to Staff",
                ["compose.action.waiting"] = "Waiting for generation",
                ["compose.action.play"] = "Play",
                ["compose.action.pause"] = "Pause",
                ["compose.action.save_audio"] = "Save Audio",
                ["compose.action.keep"] = "Keep option",
                ["compose.action.unkeep"] = "Unkeep",
                ["compose.action.save_rating"] = "Save Rating",
                ["compose.action.rate_details"] = "Rate Details",
                ["compose.meta.key"] = "Key",
                ["compose.meta.meter"] = "Meter",
                ["compose.meta.measures"] = "Measures",
                ["compose.meta.tempo"] = "Tempo",
                ["compose.meta.duration"] = "Duration",
                ["compose.meta.preference_fit"] = "Preference Fit",
                ["compose.meta.user_rating"] = "Your Rating",
                ["compose.meta.predicted_breakdown"] = "Predicted Breakdown",
                ["compose.meta.model_kind"] = "Ranking Model",
                ["compose.meta.creation_reason"] = "Creation Reason",
                ["compose.meta.ranking_reason"] = "Ranking Reason",
                ["compose.mode.major"] = "major",
                ["compose.mode.minor"] = "minor",
                ["compose.status.ready"] = "Smart Compose page is ready.",
                ["compose.status.generated"] = "Generated {0} candidate ideas.",
                ["compose.status.reranked"] = "Generated {0} candidate ideas and sorted them by preference using {1} ratings.",
                ["compose.status.applied"] = "Applied option {0} to the staff page: {1}",
                ["compose.status.rating_saved"] = "Saved rating for option {0}: {1} ({2} total ratings)",
                ["compose.status.rating_cleared"] = "Cleared rating for option {0} ({1} ratings left)",
                ["compose.status.audio_exported"] = "Saved option {0} as WAV: {1}",
                ["compose.status.audio_export_failed"] = "Failed to save audio for option {0}: {1}",
                ["compose.status.generation_failed"] = "Generation failed: {0}",
                ["compose.status.failed_short"] = "Generation failed",
                ["compose.rate.melody"] = "Melody",
                ["compose.rate.rhythm"] = "Rhythm",
                ["compose.rate.harmony"] = "Harmony",
                ["compose.rate.mood_fit"] = "Mood Fit",
                ["compose.rate.overall"] = "Overall",
                ["compose.rate.dialog_title"] = "Rate Option {0}",
                ["compose.rate.dialog_hint"] = "Score melody, rhythm, harmony, mood fit, and overall separately. Saving again replaces the old rating.",
                ["compose.rate.dialog_save"] = "Save",
                ["compose.rate.dialog_clear"] = "Clear Rating",
                ["compose.rate.dialog_cancel"] = "Cancel",
                ["compose.model.trained"] = "Local trained ranking model",
                ["compose.model.heuristic"] = "Heuristic estimate",
                ["compose.reason.separator"] = ", ",
                ["compose.reason.creation_template"] = "To stay closer to the feeling of \"{0}\", this version leans toward {1}.",
                ["compose.reason.ranking_template"] = "{0}: {1}, with {2} standing out the most, and {3}.",
                ["compose.reason.smooth_melody"] = "a smoother melodic contour",
                ["compose.reason.bold_melody"] = "bolder melodic leaps",
                ["compose.reason_spacious_rhythm"] = "more rhythmic space",
                ["compose.reason_dense_rhythm"] = "a denser rhythmic flow",
                ["compose.reason_rich_harmony"] = "richer harmony",
                ["compose.reason_light_harmony"] = "lighter harmony",
                ["compose.reason.top_rank"] = "the highest overall predicted score in this batch",
                ["compose.reason.above_average_rank"] = "an above-average overall predicted score in this batch",
                ["compose.reason.balanced_profile"] = "overall balance",
                ["compose.reason.more_mood_aligned"] = "it fits the current mood more closely",
                ["compose.reason.more_coordinated"] = "simultaneous note durations are more coordinated",
                ["compose.reason.more_stable"] = "large melodic leaps are better controlled",
                ["compose.reason.more_aligned"] = "it matches your past high-rated samples more closely",
                ["compose.reason.trained_model"] = "Trained model view",
                ["compose.reason.heuristic_model"] = "Heuristic view",
                ["settings.page_title"] = "Settings",
                ["settings.section.personalization"] = "Personalization",
                ["settings.theme"] = "Theme",
                ["settings.theme.system"] = "Use system setting",
                ["settings.theme.light"] = "Light",
                ["settings.theme.dark"] = "Dark",
                ["settings.language"] = "Display language",
                ["settings.language.system"] = "Use system setting",
                ["settings.language.zh"] = "Chinese (Simplified)",
                ["settings.language.en"] = "English",
                ["settings.section.about"] = "About",
                ["settings.about.name"] = "MusicBox",
                ["settings.about.version"] = "Version",
                ["settings.about.build"] = "Build",
                ["settings.about.author"] = "Author",
                ["settings.restart.title"] = "Restart Required",
                ["settings.restart.content"] = "Changing display language requires a restart. Unsaved changes may be lost. Restart now?",
                ["settings.restart.confirm"] = "Restart now",
                ["settings.restart.cancel"] = "Later",
                ["editor.menu.file"] = "File",
                ["editor.menu.new"] = "New",
                ["editor.menu.open"] = "Open",
                ["editor.menu.save"] = "Save",
                ["editor.menu.save_as"] = "Save As",
                ["editor.menu.import_musicxml"] = "Import MusicXML",
                ["editor.menu.export_musicxml"] = "Export MusicXML",
                ["editor.menu.export_audio"] = "Export WAV",
                ["editor.menu.export_pdf"] = "Export PDF",
                ["editor.menu.print"] = "Print...",
                ["editor.menu.time_signature"] = "Time",
                ["editor.menu.key_signature"] = "Key",
                ["editor.menu.tempo"] = "Tempo",
                ["editor.menu.note_snap"] = "Snap",
                ["editor.menu.display"] = "View",
                ["editor.menu.grid"] = "Grid",
                ["editor.menu.area_select"] = "Area Select",
                ["editor.menu.clear"] = "Clear",
                ["editor.new_confirm.title"] = "Create a new score?",
                ["editor.new_confirm.content"] = "The current score has content. Creating a new score will clear it. Continue?",
                ["editor.new_confirm.primary"] = "Create New",
                ["editor.new_confirm.cancel"] = "Cancel",
                ["editor.toolbar.undo"] = "Undo (Ctrl+Z)",
                ["editor.toolbar.redo"] = "Redo (Ctrl+Y)",
                ["editor.toolbar.expressions"] = "Expressions",
                ["editor.toolbar.pedal"] = "Pedal",
                ["editor.toolbar.slur"] = "Slur",
                ["editor.toolbar.duration"] = "Duration",
                ["editor.toolbar.note_type"] = "Note Type",
                ["editor.toolbar.add_system"] = "Add Staff",
                ["editor.toolbar.add_system_tooltip"] = "Add one staff system",
                ["editor.toolbar.play"] = "Play",
                ["editor.toolbar.pause"] = "Pause",
                ["editor.toolbar.stop"] = "Stop",
                ["editor.note_length.none"] = "None",
                ["editor.note_length.whole"] = "Whole",
                ["editor.note_length.half"] = "Half",
                ["editor.note_length.quarter"] = "Quarter",
                ["editor.note_length.eighth"] = "Eighth",
                ["editor.note_length.sixteenth"] = "16th",
                ["editor.note_length.thirty_second"] = "32nd",
                ["editor.duration_mode.note"] = "Note",
                ["editor.duration_mode.rest"] = "Rest",
                ["editor.note_type.sharp"] = "Sharp",
                ["editor.note_type.double_sharp"] = "Double Sharp",
                ["editor.note_type.flat"] = "Flat",
                ["editor.note_type.double_flat"] = "Double Flat",
                ["editor.note_type.natural"] = "Natural",
                ["editor.note_type.staccato"] = "Staccato",
                ["editor.note_type.staccatissimo"] = "Staccatissimo",
                ["editor.note_type.accent"] = "Accent",
                ["editor.note_type.dot"] = "Dot",
                ["editor.expression.cresc_symbol"] = "Crescendo Symbol",
                ["editor.expression.dim_symbol"] = "Diminuendo Symbol",
                ["editor.expression.ottava"] = "Ottava",
                ["editor.expression.pedal"] = "Pedal",
                ["editor.expression.pedal_release"] = "Pedal Release",
                ["editor.expression.pedal_line"] = "Syncopated Pedal",
                ["editor.expression.tune"] = "Natural Sign",
                ["editor.expression.stacc"] = "Staccato Mark"
            }
        };

        static LocalizationService()
        {
            AppSettingsService.Instance.SettingsChanged += (_, __) => LanguageChanged?.Invoke(null, EventArgs.Empty);
        }

        public static event EventHandler? LanguageChanged;

        public static string Translate(string key)
        {
            return TranslateForLanguage(AppSettingsService.Instance.ResolveLanguageTag(), key);
        }

        public static string TranslateForLanguage(string languageTag, string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string language = string.IsNullOrWhiteSpace(languageTag) ? "en-US" : languageTag;
            if (Resources.TryGetValue(language, out Dictionary<string, string>? table) && table.TryGetValue(key, out string? localized))
            {
                return localized;
            }

            if (Resources.TryGetValue("en-US", out Dictionary<string, string>? fallback) && fallback.TryGetValue(key, out string? english))
            {
                return english;
            }

            return key;
        }

        public static string Format(string key, params object?[] args)
        {
            string template = Translate(key);
            if (args == null || args.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
            }
        }

        public static void RegisterLanguage(string languageTag, IReadOnlyDictionary<string, string> entries)
        {
            if (string.IsNullOrWhiteSpace(languageTag) || entries == null || entries.Count == 0)
            {
                return;
            }

            if (!Resources.TryGetValue(languageTag, out Dictionary<string, string>? table))
            {
                table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Resources[languageTag] = table;
            }

            foreach (KeyValuePair<string, string> entry in entries)
            {
                table[entry.Key] = entry.Value;
            }

            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
