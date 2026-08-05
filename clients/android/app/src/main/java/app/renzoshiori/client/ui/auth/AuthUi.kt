package app.renzoshiori.client.ui.auth

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.renzoshiori.client.ui.theme.RenzoColors
import app.renzoshiori.client.ui.tv.LocalIsTv
import app.renzoshiori.client.ui.tv.focusRing
import app.renzoshiori.client.ui.tv.rememberFocusState
import app.renzoshiori.client.ui.tv.tvClickable
import app.renzoshiori.client.ui.tv.tvContentColor

/**
 * The web app's shadcn primitives (card / label / input / button / checkbox /
 * the two coloured notice boxes), transliterated 1:1 into Compose so every
 * auth screen in this package is laid out from the same parts its
 * RenzoFrontend page is. Numbers are the Tailwind classes spelled out:
 * max-w-md = 448, mx-4 = 16, p-6 = 24, space-y-4 = 16, h-9 = 36, h-14 = 56,
 * rounded-xl = 12, rounded-md = 6, rounded-sm = 4, text-sm = 14sp.
 */

/** bg-red-950 / text-red-500 — the error notice surface used by every auth page. */
internal val AuthErrorSurface = Color(0xFF450A0A)
internal val AuthErrorText = Color(0xFFEF4444)

private val CardShape = RoundedCornerShape(12.dp)
private val ControlShape = RoundedCornerShape(6.dp)

/**
 * `flex items-center justify-center min-h-screen bg-background` + the page's
 * `w-full max-w-md mx-4` card column. Scrolls and lifts above the IME so the
 * password fields stay visible on short phones.
 */
@Composable
fun AuthPageScaffold(content: @Composable ColumnScope.() -> Unit) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(RenzoColors.Background)
            // No imePadding(): the activity is adjustResize and the card lives
            // in a scroller, so a focused field brings itself into view without
            // double-counting the keyboard inset.
            .verticalScroll(rememberScrollState())
            .padding(vertical = 24.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            modifier = Modifier
                .widthIn(max = 448.dp)
                .fillMaxWidth()
                .padding(horizontal = 16.dp),
        ) {
            content()
        }
    }
}

/** `rounded-xl border bg-card text-card-foreground shadow` */
@Composable
fun AuthCard(content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(CardShape)
            .background(RenzoColors.Card)
            .border(1.dp, RenzoColors.Border, CardShape),
    ) {
        content()
    }
}

/** `flex flex-col space-y-1.5 p-6` — [spacing] carries each page's space-y-N. */
@Composable
fun AuthCardHeader(spacing: Dp = 6.dp, content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier.fillMaxWidth().padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(spacing),
    ) {
        content()
    }
}

/** `p-6 pt-0` */
@Composable
fun AuthCardContent(content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier = Modifier.fillMaxWidth().padding(start = 24.dp, end = 24.dp, bottom = 24.dp),
    ) {
        content()
    }
}

/** CardTitle — `text-2xl font-bold text-center` as used by the three password pages. */
@Composable
fun AuthCardTitle(text: String) {
    Text(
        text,
        color = RenzoColors.Foreground,
        fontSize = 24.sp,
        lineHeight = 28.sp,
        fontWeight = FontWeight.Bold,
        textAlign = TextAlign.Center,
        style = MaterialTheme.typography.titleLarge,
        modifier = Modifier.fillMaxWidth(),
    )
}

/** CardDescription — `text-sm text-muted-foreground` */
@Composable
fun AuthCardDescription(text: String, center: Boolean = true) {
    Text(
        text,
        color = RenzoColors.MutedForeground,
        fontSize = 14.sp,
        lineHeight = 20.sp,
        textAlign = if (center) TextAlign.Center else TextAlign.Start,
        style = MaterialTheme.typography.bodyMedium,
        modifier = Modifier.fillMaxWidth(),
    )
}

/** Label — `text-sm font-medium leading-none` */
@Composable
fun AuthLabel(text: String) {
    Text(
        text,
        color = RenzoColors.Foreground,
        fontSize = 14.sp,
        fontWeight = FontWeight.Medium,
        style = MaterialTheme.typography.labelLarge,
    )
}

/**
 * Input — `flex h-9 w-full rounded-md border border-input bg-transparent px-3
 * py-1 text-sm placeholder:text-muted-foreground focus-visible:ring-1
 * ring-ring`. Deliberately NOT an OutlinedTextField: Material's floating label
 * and 56dp height are the loudest "this isn't our app" tell after pill buttons.
 */
@Composable
fun AuthInput(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
    isPassword: Boolean = false,
    keyboardType: KeyboardType = KeyboardType.Text,
    imeAction: ImeAction = ImeAction.Next,
    onImeAction: () -> Unit = {},
    singleLine: Boolean = true,
) {
    var focused by remember { mutableStateOf(false) }
    val textStyle = MaterialTheme.typography.bodyMedium.copy(
        color = RenzoColors.Foreground,
        fontSize = 14.sp,
    )
    BasicTextField(
        value = value,
        onValueChange = onValueChange,
        singleLine = singleLine,
        textStyle = textStyle,
        cursorBrush = SolidColor(RenzoColors.Foreground),
        visualTransformation = if (isPassword) PasswordVisualTransformation() else VisualTransformation.None,
        keyboardOptions = KeyboardOptions(
            keyboardType = if (isPassword) KeyboardType.Password else keyboardType,
            imeAction = imeAction,
        ),
        // onNext is deliberately left unhandled so Compose's default
        // "advance to the next field" behavior survives.
        keyboardActions = KeyboardActions(
            onDone = { onImeAction() },
            onGo = { onImeAction() },
            onSend = { onImeAction() },
        ),
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 36.dp)
            .onFocusChanged { focused = it.isFocused }
            .clip(ControlShape)
            .border(
                width = 1.dp,
                color = if (focused) RenzoColors.Primary else RenzoColors.Border,
                shape = ControlShape,
            )
            // A 1dp accent border is the right weight at arm's length and
            // invisible across a room; TV gets the 3dp ring on top of it.
            .focusRing(LocalIsTv.current && focused, 6.dp)
            .padding(horizontal = 12.dp, vertical = 8.dp),
        decorationBox = { inner ->
            Box(contentAlignment = Alignment.CenterStart) {
                if (value.isEmpty()) {
                    Text(
                        placeholder,
                        color = RenzoColors.MutedForeground,
                        fontSize = 14.sp,
                        style = MaterialTheme.typography.bodyMedium,
                    )
                }
                inner()
            }
        },
    )
}

/** Button variant="default" — `bg-primary text-primary-foreground h-9 px-4 py-2 text-sm font-medium`. */
@Composable
fun AuthPrimaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
) {
    var focused by remember { mutableStateOf(false) }
    val isTv = LocalIsTv.current
    Button(
        onClick = onClick,
        enabled = enabled,
        shape = MaterialTheme.shapes.small,
        colors = ButtonDefaults.buttonColors(
            containerColor = RenzoColors.Primary,
            contentColor = RenzoColors.PrimaryForeground,
            // disabled:opacity-50, and only ever while a request is in flight.
            disabledContainerColor = RenzoColors.Primary.copy(alpha = 0.5f),
            disabledContentColor = RenzoColors.PrimaryForeground.copy(alpha = 0.5f),
        ),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 36.dp)
            .onFocusChanged { focused = it.isFocused }
            .focusRing(isTv && focused, 8.dp),
    ) {
        Text(text, fontSize = 14.sp, fontWeight = FontWeight.Medium, style = MaterialTheme.typography.labelLarge)
    }
}

/** Button variant="outline" — `border border-input bg-background`. */
@Composable
fun AuthOutlineButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var focused by remember { mutableStateOf(false) }
    val isTv = LocalIsTv.current
    OutlinedButton(
        onClick = onClick,
        shape = MaterialTheme.shapes.small,
        border = BorderStroke(1.dp, RenzoColors.Border),
        colors = ButtonDefaults.outlinedButtonColors(
            containerColor = RenzoColors.Background,
            contentColor = RenzoColors.Foreground,
        ),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = 36.dp)
            .onFocusChanged { focused = it.isFocused }
            .focusRing(isTv && focused, 8.dp),
    ) {
        Text(text, fontSize = 14.sp, fontWeight = FontWeight.Medium, style = MaterialTheme.typography.labelLarge)
    }
}

/** `p-3 text-sm text-red-500 bg-red-950 rounded-md` */
@Composable
fun AuthErrorBox(message: String) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .clip(ControlShape)
            .background(AuthErrorSurface)
            .padding(12.dp),
    ) {
        Text(message, color = AuthErrorText, fontSize = 14.sp, lineHeight = 20.sp, style = MaterialTheme.typography.bodyMedium)
    }
}

/** `p-3 text-sm rounded-md bg-muted text-muted-foreground` */
@Composable
fun AuthNoticeBox(message: String) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .clip(ControlShape)
            .background(RenzoColors.Muted)
            .padding(12.dp),
    ) {
        Text(message, color = RenzoColors.MutedForeground, fontSize = 14.sp, lineHeight = 20.sp, style = MaterialTheme.typography.bodyMedium)
    }
}

/**
 * The shadcn Checkbox + its clickable Label: `h-4 w-4 rounded-sm border
 * border-primary`, filled with bg-primary + a check when on, `space-x-2` to a
 * `text-sm cursor-pointer` label.
 */
@Composable
fun AuthCheckboxRow(
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    label: String,
) {
    val interaction = remember { MutableInteractionSource() }
    val focus = rememberFocusState()
    val isTv = LocalIsTv.current
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier
            .clip(ControlShape)
            .focusRing(isTv && focus.focused, 6.dp)
            .then(
                if (isTv) {
                    Modifier.tvClickable(onFocused = focus::set) { onCheckedChange(!checked) }
                } else {
                    Modifier.clickable(interactionSource = interaction, indication = null) {
                        onCheckedChange(!checked)
                    }
                },
            )
            .padding(vertical = 4.dp),
    ) {
        Box(
            contentAlignment = Alignment.Center,
            modifier = Modifier
                .size(16.dp)
                .clip(RoundedCornerShape(4.dp))
                .background(if (checked) RenzoColors.Primary else Color.Transparent)
                .border(1.dp, RenzoColors.Primary, RoundedCornerShape(4.dp)),
        ) {
            if (checked) {
                Icon(
                    Icons.Filled.Check,
                    contentDescription = null,
                    tint = RenzoColors.PrimaryForeground,
                    modifier = Modifier.size(12.dp),
                )
            }
        }
        Text(label, color = RenzoColors.Foreground, fontSize = 14.sp, style = MaterialTheme.typography.bodyMedium)
    }
}

/**
 * The `text-muted-foreground hover:text-foreground` inline links at the foot of
 * every auth card ("Forgot password?", "Back to login").
 */
@Composable
fun AuthLinkRow(text: String, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val focus = rememberFocusState()
    val isTv = LocalIsTv.current
    Box(
        modifier = Modifier.fillMaxWidth(),
        contentAlignment = Alignment.Center,
    ) {
        Text(
            text,
            color = if (isTv) tvContentColor(false, focus.focused) else RenzoColors.MutedForeground,
            fontSize = 14.sp,
            textAlign = TextAlign.Center,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier
                .clip(ControlShape)
                .focusRing(isTv && focus.focused, 6.dp)
                .then(
                    if (isTv) {
                        Modifier.tvClickable(onFocused = focus::set) { onClick() }
                    } else {
                        Modifier.clickable(interactionSource = interaction, indication = null) { onClick() }
                    },
                )
                .padding(vertical = 6.dp, horizontal = 8.dp),
        )
    }
}

/**
 * Reset/invite tokens reach a phone by pasting the whole emailed link
 * (`…/auth/reset-password?token=abc`), so pull the value out rather than making
 * the user surgically select it. A bare token is returned unchanged.
 */
fun extractQueryValue(pasted: String, key: String): String {
    val text = pasted.trim()
    val marker = "$key="
    val at = text.indexOf(marker)
    if (at < 0) return text
    val rest = text.substring(at + marker.length)
    return rest.takeWhile { it != '&' && it != '#' && !it.isWhitespace() }
}
