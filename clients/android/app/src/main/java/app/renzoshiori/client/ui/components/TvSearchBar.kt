package app.renzoshiori.client.ui.components

import android.content.ActivityNotFoundException
import android.content.Intent
import android.speech.RecognizerIntent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState

/**
 * Voice search, resolved defensively.
 *
 * Not every television ships a speech recogniser — plenty of cheap sets and
 * most sideloaded/AOSP boxes have none — so this resolves the intent up front
 * and returns `null` when nothing handles it. Callers hide the mic entirely in
 * that case rather than offering a button that throws.
 *
 * Requires the `<queries>` entry for `android.speech.action.RECOGNIZE_SPEECH`
 * in the manifest, or package-visibility filtering makes `resolveActivity`
 * return null on API 30+ and the mic never appears at all.
 *
 * The transcript is handed back as a *query*, never a selection: romanised
 * Japanese titles come back mangled far more often than English, so the caller
 * must land the user on results they confirm with the D-pad, and must leave the
 * transcript editable in the field so it can be corrected without starting over.
 */
@Composable
fun rememberVoiceSearch(prompt: String, onTranscript: (String) -> Unit): (() -> Unit)? {
    val context = LocalContext.current
    val intent = remember(prompt) {
        Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
            putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            putExtra(RecognizerIntent.EXTRA_PROMPT, prompt)
        }
    }
    val available = remember(intent) {
        runCatching { context.packageManager.resolveActivity(intent, 0) != null }.getOrDefault(false)
    }
    val launcher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult(),
    ) { result ->
        result.data
            ?.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS)
            ?.firstOrNull()
            ?.takeIf { it.isNotBlank() }
            ?.let(onTranscript)
    }
    if (!available) return null
    return {
        // NOTE for the Hub port: the Hub must wrap this launch in
        // HubForeground.leavingApp() — backgrounding the activity otherwise
        // bounces it back to the app picker. That call does not exist in this
        // standalone client and is deliberately absent here.
        try {
            launcher.launch(intent)
        } catch (_: ActivityNotFoundException) {
            // Resolved a moment ago, uninstalled since — nothing useful to do.
        }
    }
}

/**
 * The TV search field: a bordered box with the text field in its `decorationBox`,
 * submitting on the IME Search action — the shape the leanback IME expects, and
 * the one `tv-native` already uses. Optional mic button, present only when the
 * device actually has a recogniser.
 *
 * This is a TV affordance; the touch build keeps the shell's command-bar search
 * untouched, so callers gate it on `LocalIsTv`.
 */
@Composable
fun TvSearchBar(
    value: String,
    onValueChange: (String) -> Unit,
    onSubmit: (String) -> Unit,
    placeholder: String,
    voicePrompt: String,
    modifier: Modifier = Modifier,
) {
    val focus = rememberFocusState()
    val micFocus = rememberFocusState()

    // Voice fills the field and runs the search, but leaves the text editable —
    // a mangled transcript is corrected in place, not by starting over.
    val startVoice = rememberVoiceSearch(voicePrompt) { transcript ->
        onValueChange(transcript)
        onSubmit(transcript)
    }

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp),
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .weight(1f)
                .height(48.dp)
                .clip(RoundedCornerShape(10.dp))
                .background(RenzoColors.Card)
                .focusRing(focus.focused, 10.dp)
                .padding(horizontal = 14.dp),
        ) {
            Icon(
                Icons.Filled.Search,
                contentDescription = null,
                tint = if (focus.focused) RenzoColors.Primary else RenzoColors.MutedForeground,
                modifier = Modifier.size(20.dp),
            )
            BasicTextField(
                value = value,
                onValueChange = onValueChange,
                singleLine = true,
                textStyle = MaterialTheme.typography.bodyLarge.copy(color = RenzoColors.Foreground),
                cursorBrush = SolidColor(RenzoColors.Foreground),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(
                    // Deliberately no clearFocus(): dropping focus on a TV leaves
                    // the cursor nowhere, and the leanback IME is a full-screen
                    // editor that closes itself on the Search action anyway.
                    onSearch = { onSubmit(value) },
                ),
                decorationBox = { inner ->
                    Box(modifier = Modifier.padding(start = 12.dp)) {
                        if (value.isEmpty()) {
                            Text(
                                placeholder,
                                style = MaterialTheme.typography.bodyLarge,
                                color = RenzoColors.MutedForeground,
                            )
                        }
                        inner()
                    }
                },
                modifier = Modifier
                    .weight(1f)
                    .onFocusChanged { focus.set(it.isFocused) },
            )
        }

        if (startVoice != null) {
            Box(
                contentAlignment = Alignment.Center,
                modifier = Modifier
                    .padding(start = 10.dp)
                    .size(48.dp)
                    .tvFocusTarget(
                        focused = micFocus.focused,
                        onFocused = micFocus::set,
                        radius = 10.dp,
                        fill = RenzoColors.Card,
                        onClick = startVoice,
                    ),
            ) {
                Icon(
                    Icons.Filled.Mic,
                    contentDescription = "Search by voice",
                    tint = if (micFocus.focused) RenzoColors.Primary else RenzoColors.MutedForeground,
                    modifier = Modifier.size(22.dp),
                )
            }
        }
    }
}
