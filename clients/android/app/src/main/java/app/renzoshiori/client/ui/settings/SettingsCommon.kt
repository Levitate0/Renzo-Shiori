package app.renzoshiori.client.ui.settings

import android.graphics.BitmapFactory
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.renzoshiori.client.ui.theme.GeistFamily
import app.renzoshiori.client.ui.theme.RenzoColors
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import retrofit2.HttpException

/**
 * Transliterations of the web app's shadcn primitives (card.tsx, input.tsx,
 * label.tsx, switch.tsx, button.tsx, badge.tsx, sheet.tsx) used by every
 * settings/account screen. Material's own defaults — pill buttons, 56dp text
 * fields, purple accents — are exactly what makes a native port stop looking
 * like Renzo Shiori, so nothing here is a Material default.
 */

// ── Section nav (settings-section-nav.tsx) ───────────────────────────────

data class SettingsNavSection(val id: String, val title: String, val icon: ImageVector)

/**
 * The web nav is a sidebar at `lg` and a full drawer below it. The phone only
 * ever sees the narrow branch, so this is exactly that: a full-width trigger
 * showing the active section, opening the same list in a drawer.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsSectionNav(
    sections: List<SettingsNavSection>,
    activeId: String,
    onChange: (String) -> Unit,
    drawerTitle: String,
) {
    var open by remember { mutableStateOf(false) }
    val active = sections.firstOrNull { it.id == activeId }
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
            .clickable { open = true }
            .padding(horizontal = 14.dp, vertical = 11.dp),
    ) {
        if (active != null) {
            Icon(
                active.icon, contentDescription = null,
                tint = RenzoColors.Foreground, modifier = Modifier.size(16.dp),
            )
        }
        Text(
            active?.title ?: "Choose a section",
            style = MaterialTheme.typography.labelLarge,
            color = RenzoColors.Foreground,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 8.dp).weight(1f),
        )
        Icon(
            Icons.Filled.Menu, contentDescription = null,
            tint = RenzoColors.MutedForeground.copy(alpha = 0.6f), modifier = Modifier.size(16.dp),
        )
    }

    if (open) {
        ModalBottomSheet(
            onDismissRequest = { open = false },
            sheetState = sheetState,
            containerColor = RenzoColors.Popover,
            contentColor = RenzoColors.Foreground,
        ) {
            Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp).padding(bottom = 28.dp)) {
                Text(
                    drawerTitle,
                    style = MaterialTheme.typography.titleLarge,
                    color = RenzoColors.Foreground,
                    modifier = Modifier.padding(bottom = 12.dp),
                )
                sections.forEach { section ->
                    val isActive = section.id == activeId
                    Box {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 2.dp)
                                .clip(RoundedCornerShape(8.dp))
                                .background(if (isActive) RenzoColors.Primary.copy(alpha = 0.1f) else Color.Transparent)
                                .clickable {
                                    onChange(section.id)
                                    open = false
                                }
                                .padding(horizontal = 12.dp, vertical = 10.dp),
                        ) {
                            Icon(
                                section.icon, contentDescription = null,
                                tint = if (isActive) RenzoColors.Primary else RenzoColors.MutedForeground,
                                modifier = Modifier.size(16.dp),
                            )
                            Text(
                                section.title,
                                style = MaterialTheme.typography.labelLarge,
                                color = if (isActive) RenzoColors.Primary else RenzoColors.MutedForeground,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.padding(start = 10.dp),
                            )
                        }
                        if (isActive) {
                            // The web's active marker: a 2px rose bar hugging the left edge.
                            Box(
                                modifier = Modifier
                                    .padding(vertical = 10.dp)
                                    .width(2.dp)
                                    .height(20.dp)
                                    .clip(RoundedCornerShape(1.dp))
                                    .background(RenzoColors.Primary)
                                    .align(Alignment.CenterStart),
                            )
                        }
                    }
                }
            }
        }
    }
}

// ── Page chrome ──────────────────────────────────────────────────────────

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScaffold(
    title: String,
    onBack: () -> Unit,
    snackbar: SnackbarHostState,
    content: @Composable (PaddingValues) -> Unit,
) {
    Scaffold(
        containerColor = RenzoColors.Background,
        snackbarHost = { SnackbarHost(snackbar) },
        topBar = {
            TopAppBar(
                title = { Text(title, style = MaterialTheme.typography.titleLarge) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = RenzoColors.Background,
                    titleContentColor = RenzoColors.Foreground,
                    navigationIconContentColor = RenzoColors.Foreground,
                ),
            )
        },
        content = content,
    )
}

/** The web page header: `h1` + a muted one-liner underneath. */
@Composable
fun PageHeading(title: String, subtitle: String) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Text(title, style = MaterialTheme.typography.headlineSmall, color = RenzoColors.Foreground)
        Text(
            subtitle,
            style = MaterialTheme.typography.bodySmall,
            color = RenzoColors.MutedForeground,
            modifier = Modifier.padding(top = 4.dp),
        )
    }
}

/** card.tsx — rounded-xl border bg-card, with the CardHeader title/description. */
@Composable
fun SettingsCard(
    title: String? = null,
    description: String? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
            .background(RenzoColors.Card)
            .padding(16.dp),
    ) {
        if (title != null) {
            Text(title, style = MaterialTheme.typography.titleMedium, color = RenzoColors.Foreground)
        }
        if (description != null) {
            Text(
                description,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.MutedForeground,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
        if (title != null || description != null) Spacer(Modifier.height(14.dp))
        content()
    }
}

/** The web's `border-t pt-4` separator between blocks inside one card. */
@Composable
fun CardDivider(top: Int = 16, bottom: Int = 16) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(top = top.dp, bottom = bottom.dp)
            .height(1.dp)
            .background(RenzoColors.Border),
    )
}

// ── Form controls ────────────────────────────────────────────────────────

@Composable
fun FieldLabel(text: String, icon: ImageVector? = null) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(bottom = 6.dp)) {
        if (icon != null) {
            Icon(
                icon, contentDescription = null,
                tint = RenzoColors.Foreground, modifier = Modifier.padding(end = 6.dp).size(14.dp),
            )
        }
        Text(text, style = MaterialTheme.typography.labelLarge, color = RenzoColors.Foreground)
    }
}

/** input.tsx — a 36dp-tall bordered field, NOT Material's 56dp TextField. */
@Composable
fun RenzoTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String = "",
    password: Boolean = false,
    keyboardType: KeyboardType = KeyboardType.Text,
    singleLine: Boolean = true,
    minLines: Int = 1,
    readOnly: Boolean = false,
    monospace: Boolean = false,
) {
    val interaction = remember { MutableInteractionSource() }
    val focused by interaction.collectIsFocusedAsState()
    BasicTextField(
        value = value,
        onValueChange = onValueChange,
        readOnly = readOnly,
        singleLine = singleLine,
        minLines = minLines,
        textStyle = MaterialTheme.typography.bodyMedium.copy(
            color = if (readOnly) RenzoColors.MutedForeground else RenzoColors.Foreground,
            fontFamily = if (monospace) FontFamily.Monospace else GeistFamily,
        ),
        cursorBrush = SolidColor(RenzoColors.Primary),
        interactionSource = interaction,
        visualTransformation = if (password) PasswordVisualTransformation() else VisualTransformation.None,
        keyboardOptions = KeyboardOptions(keyboardType = keyboardType),
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .background(if (readOnly) RenzoColors.Muted.copy(alpha = 0.4f) else Color.Transparent)
            .border(
                1.dp,
                if (focused) RenzoColors.Primary else RenzoColors.Border,
                RoundedCornerShape(8.dp),
            )
            .padding(horizontal = 12.dp, vertical = 9.dp),
        decorationBox = { inner ->
            Box(contentAlignment = Alignment.CenterStart) {
                if (value.isEmpty() && placeholder.isNotEmpty()) {
                    Text(
                        placeholder,
                        style = MaterialTheme.typography.bodyMedium,
                        color = RenzoColors.MutedForeground,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                inner()
            }
        },
    )
}

/** A labelled field + optional muted hint, the web's `space-y-2` block. */
@Composable
fun LabelledField(
    label: String,
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String = "",
    hint: String? = null,
    password: Boolean = false,
    keyboardType: KeyboardType = KeyboardType.Text,
    readOnly: Boolean = false,
    monospace: Boolean = false,
    icon: ImageVector? = null,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp)) {
        FieldLabel(label, icon)
        RenzoTextField(
            value = value,
            onValueChange = onValueChange,
            placeholder = placeholder,
            password = password,
            keyboardType = keyboardType,
            readOnly = readOnly,
            monospace = monospace,
        )
        if (hint != null) Hint(hint)
    }
}

/** A numeric field that clamps to [min]..[max] exactly like the web inputs. */
@Composable
fun NumberField(
    label: String,
    value: Int,
    min: Int,
    max: Int,
    onValueChange: (Int) -> Unit,
    hint: String? = null,
    warning: String? = null,
) {
    // Seeded once, NOT keyed on `value`: the callback clamps to min..max, so a
    // keyed remember would rewrite the box mid-keystroke ("1" → "3" while the
    // user is still typing "12").
    var text by remember { mutableStateOf(value.toString()) }
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp)) {
        FieldLabel(label)
        RenzoTextField(
            value = text,
            onValueChange = { raw ->
                text = raw.filter { it.isDigit() }
                val parsed = text.toIntOrNull()
                if (parsed != null) onValueChange(parsed.coerceIn(min, max))
            },
            keyboardType = KeyboardType.Number,
        )
        if (hint != null) Hint(hint)
        if (warning != null) {
            Text(
                warning,
                style = MaterialTheme.typography.bodySmall,
                color = RenzoColors.Amber,
                fontWeight = FontWeight.Medium,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
    }
}

/** select.tsx — a full-width bordered trigger + a Popover-coloured menu. */
@Composable
fun RenzoSelect(
    options: List<Pair<String, String>>, // value to label
    value: String,
    onChange: (String) -> Unit,
    placeholder: String = "Choose…",
    modifier: Modifier = Modifier,
) {
    var open by remember { mutableStateOf(false) }
    val selected = options.firstOrNull { it.first == value }
    Box(modifier = modifier.fillMaxWidth()) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(8.dp))
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(8.dp))
                .clickable { open = true }
                .padding(horizontal = 12.dp, vertical = 9.dp),
        ) {
            Text(
                selected?.second ?: placeholder,
                style = MaterialTheme.typography.bodyMedium,
                color = if (selected == null) RenzoColors.MutedForeground else RenzoColors.Foreground,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Icon(
                Icons.Filled.ExpandMore, contentDescription = null,
                tint = RenzoColors.MutedForeground, modifier = Modifier.size(16.dp),
            )
        }
        DropdownMenu(
            expanded = open,
            onDismissRequest = { open = false },
            containerColor = RenzoColors.Popover,
        ) {
            options.forEach { (optValue, optLabel) ->
                DropdownMenuItem(
                    text = {
                        Text(optLabel, style = MaterialTheme.typography.bodyMedium, color = RenzoColors.Foreground)
                    },
                    onClick = {
                        onChange(optValue)
                        open = false
                    },
                )
            }
        }
    }
}

@Composable
fun Hint(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = RenzoColors.MutedForeground,
        modifier = Modifier.padding(top = 4.dp),
    )
}

/** switch.tsx + its label, the web's `flex items-center space-x-2` row. */
@Composable
fun SwitchRow(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    label: String,
    hint: String? = null,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Switch(
                checked = checked,
                onCheckedChange = onCheckedChange,
                colors = SwitchDefaults.colors(
                    checkedThumbColor = RenzoColors.PrimaryForeground,
                    checkedTrackColor = RenzoColors.Primary,
                    checkedBorderColor = RenzoColors.Primary,
                    uncheckedThumbColor = RenzoColors.MutedForeground,
                    uncheckedTrackColor = RenzoColors.Secondary,
                    uncheckedBorderColor = RenzoColors.Border,
                ),
            )
            Text(
                label,
                style = MaterialTheme.typography.labelLarge,
                color = RenzoColors.Foreground,
                modifier = Modifier.padding(start = 10.dp),
            )
        }
        if (hint != null) Hint(hint)
    }
}

/** radio-group.tsx row — a ring circle, label, and muted trailing note. */
@Composable
fun RadioRow(selected: Boolean, label: String, note: String, onSelect: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().clickable(onClick = onSelect).padding(vertical = 6.dp),
    ) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(16.dp)
                .clip(CircleShape)
                .border(1.dp, if (selected) RenzoColors.Primary else RenzoColors.Border, CircleShape),
        ) {
            if (selected) {
                Box(modifier = Modifier.size(8.dp).clip(CircleShape).background(RenzoColors.Primary))
            }
        }
        Column(modifier = Modifier.padding(start = 10.dp)) {
            Text(label, style = MaterialTheme.typography.bodyMedium, color = RenzoColors.Foreground)
            Text(note, style = MaterialTheme.typography.bodySmall, color = RenzoColors.MutedForeground)
        }
    }
}

// ── Buttons (button.tsx variants; never Material's pill shape) ────────────

@Composable
fun RenzoButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    variant: String = "default", // default | outline | secondary | ghost | destructive
    busy: Boolean = false,
    small: Boolean = false,
) {
    val bg = when (variant) {
        "default" -> RenzoColors.Primary
        "secondary" -> RenzoColors.Secondary
        "destructive" -> RenzoColors.Red
        else -> Color.Transparent
    }
    val fg = when (variant) {
        "default" -> RenzoColors.PrimaryForeground
        "destructive" -> Color.White
        "ghost" -> RenzoColors.Foreground
        else -> RenzoColors.Foreground
    }
    val shape = RoundedCornerShape(8.dp)
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
        modifier = modifier
            .clip(shape)
            .background(bg)
            .then(if (variant == "outline") Modifier.border(1.dp, RenzoColors.Border, shape) else Modifier)
            .clickable(enabled = !busy, onClick = onClick)
            .padding(horizontal = if (small) 10.dp else 14.dp, vertical = if (small) 7.dp else 9.dp),
    ) {
        val gap = if (text.isEmpty()) 0.dp else 8.dp
        if (busy) {
            CircularProgressIndicator(
                color = fg, strokeWidth = 2.dp,
                modifier = Modifier.padding(end = gap).size(14.dp),
            )
        } else if (icon != null) {
            Icon(icon, contentDescription = null, tint = fg, modifier = Modifier.padding(end = gap).size(15.dp))
        }
        if (text.isNotEmpty()) {
            Text(text, style = MaterialTheme.typography.labelLarge, color = fg, maxLines = 1)
        }
    }
}

/** An icon-only ghost button (the web's `variant="ghost" size="sm"` actions). */
@Composable
fun IconGhostButton(icon: ImageVector, contentDescription: String, tint: Color, onClick: () -> Unit) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(32.dp)
            .clip(RoundedCornerShape(8.dp))
            .clickable(onClick = onClick),
    ) {
        Icon(icon, contentDescription = contentDescription, tint = tint, modifier = Modifier.size(16.dp))
    }
}

// ── Badges + status blocks ───────────────────────────────────────────────

@Composable
fun RenzoBadge(text: String, color: Color, filled: Boolean = false, icon: ImageVector? = null) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(if (filled) color else color.copy(alpha = 0.15f))
            .then(if (filled) Modifier else Modifier.border(1.dp, color.copy(alpha = 0.35f), RoundedCornerShape(50)))
            .padding(horizontal = 8.dp, vertical = 3.dp),
    ) {
        if (icon != null) {
            Icon(
                icon, contentDescription = null,
                tint = if (filled) Color.White else color,
                modifier = Modifier.padding(end = 4.dp).size(11.dp),
            )
        }
        Text(
            text,
            style = MaterialTheme.typography.labelSmall,
            color = if (filled) Color.White else color,
            maxLines = 1,
        )
    }
}

/** The web's `p-3 text-sm text-red-500 bg-red-950 rounded-md` error block. */
@Composable
fun ErrorBox(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = RenzoColors.Red,
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 10.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(Color(0xFF450A0A))
            .padding(12.dp),
    )
}

@Composable
fun SuccessBox(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = RenzoColors.Emerald,
        modifier = Modifier
            .fillMaxWidth()
            .padding(bottom = 10.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(Color(0xFF052E16))
            .padding(12.dp),
    )
}

/** The web's dashed empty-state paragraph. */
@Composable
fun EmptyNote(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodySmall,
        color = RenzoColors.MutedForeground,
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(8.dp))
            .border(1.dp, RenzoColors.Border.copy(alpha = 0.6f), RoundedCornerShape(8.dp))
            .background(RenzoColors.Card.copy(alpha = 0.4f))
            .padding(16.dp),
    )
}

@Composable
fun LoadingBlock(text: String = "Loading…") {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp),
    ) {
        CircularProgressIndicator(color = RenzoColors.MutedForeground, strokeWidth = 2.dp, modifier = Modifier.size(16.dp))
        Text(
            text,
            style = MaterialTheme.typography.bodyMedium,
            color = RenzoColors.MutedForeground,
            modifier = Modifier.padding(start = 10.dp),
        )
    }
}

// ── Dialog (dialog.tsx / responsive-modal.tsx) ────────────────────────────

@Composable
fun RenzoDialog(
    onDismiss: () -> Unit,
    title: String,
    description: String? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(max = 620.dp)
                .clip(RoundedCornerShape(12.dp))
                .background(RenzoColors.Popover)
                .border(1.dp, RenzoColors.Border, RoundedCornerShape(12.dp))
                .padding(20.dp)
                .verticalScroll(rememberScrollState()),
        ) {
            Text(title, style = MaterialTheme.typography.titleMedium, color = RenzoColors.Foreground)
            if (description != null) {
                Text(
                    description,
                    style = MaterialTheme.typography.bodySmall,
                    color = RenzoColors.MutedForeground,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
            Spacer(Modifier.height(16.dp))
            content()
        }
    }
}

// ── Avatar ───────────────────────────────────────────────────────────────

fun decodeAvatar(base64: String?): ImageBitmap? =
    base64?.takeIf { it.isNotBlank() }?.let { raw ->
        runCatching {
            val bytes = android.util.Base64.decode(raw, android.util.Base64.DEFAULT)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap()
        }.getOrNull()
    }

@Composable
fun Avatar(image: ImageBitmap?, initials: String, size: Int = 64) {
    Box(
        contentAlignment = Alignment.Center,
        modifier = Modifier
            .size(size.dp)
            .clip(CircleShape)
            .background(RenzoColors.Primary.copy(alpha = 0.2f))
            .border(1.dp, RenzoColors.Primary.copy(alpha = 0.3f), CircleShape),
    ) {
        if (image != null) {
            androidx.compose.foundation.Image(
                bitmap = image,
                contentDescription = "Avatar",
                contentScale = androidx.compose.ui.layout.ContentScale.Crop,
                modifier = Modifier.fillMaxSize().clip(CircleShape),
            )
        } else {
            Text(
                initials,
                style = MaterialTheme.typography.titleMedium,
                color = RenzoColors.Primary,
            )
        }
    }
}

// ── Errors ───────────────────────────────────────────────────────────────

private val errorJson = Json { ignoreUnknownKeys = true }

/**
 * The backend answers failures with `{ "error": "…" }` (and ProblemDetails
 * `{ "title": … }` for framework-level ones). Surfacing that text instead of a
 * generic "request failed" is the whole reason a user can tell a wrong password
 * from an expired session.
 */
fun Throwable.apiMessage(fallback: String): String {
    if (this is HttpException) {
        val body = runCatching { response()?.errorBody()?.string() }.getOrNull()
        if (!body.isNullOrBlank()) {
            val parsed = runCatching {
                val obj = errorJson.parseToJsonElement(body).jsonObject
                (obj["error"] ?: obj["message"] ?: obj["detail"] ?: obj["title"])?.jsonPrimitive?.content
            }.getOrNull()
            if (!parsed.isNullOrBlank()) return parsed
        }
        return "$fallback (HTTP ${code()})"
    }
    return message?.takeIf { it.isNotBlank() } ?: fallback
}
