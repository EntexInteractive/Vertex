import {Icon} from '../../components/Icon'
import {type ChangedFile, sampleDiff} from '../../mocks/sampleData'

/** The right column of the Workspace page: what changed in the selected file. */
export function DiffPane({file}: {file: ChangedFile}) {
    return (
        <section className="diff-pane">
            <div className="diff-head">
                <div>
                    <Icon name="file" size={15}/>
                    <span>
                        <b>{file.path.split('/').pop()}</b>
                        <small>{file.path}</small>
                    </span>
                </div>
                <div className="diff-actions">
                    <span className="diff-stat add">+{file.add}</span>
                    <span className="diff-stat del">−{file.del}</span>
                </div>
            </div>
            <div className="diff-subbar">
                <span>Changes in your workspace since the last sync</span>
            </div>
            <div className="code-view">
                <div className="hunk">@@ -18,11 +18,13 @@ struct FEditorPanelArgs</div>
                {sampleDiff.map(([oldLine, newLine, text], index) => {
                    const kind = text.startsWith('+') ? 'added-line' : text.startsWith('-') ? 'deleted-line' : ''
                    return (
                        <div className={`code-row ${kind}`} key={`${oldLine}-${newLine}-${index}`}>
                            <span>{oldLine}</span>
                            <span>{newLine}</span>
                            <code>{text}</code>
                        </div>
                    )
                })}
            </div>
        </section>
    )
}
